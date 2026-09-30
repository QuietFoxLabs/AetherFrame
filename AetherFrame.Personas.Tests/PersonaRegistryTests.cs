using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Personas.Storage;
using AetherFrame.Protocol.Documents;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The manager over a registry (decision P3): it loads what was saved; it refuses a registry it
/// cannot read and never replaces it; it saves every change before applying it, so memory never
/// shows a change the registry has not accepted (a failed save is not applied, though a restart
/// may show it when the save's outcome is unknown); it keeps the registry to 256 personas, checked before any key
/// is committed; it keeps K4's acknowledgement; it signs only for the persona an operation showed
/// (L10); and its listings never wait on a save. Every key is synthetic and in memory, and so is the
/// registry.
/// </summary>
public sealed class PersonaRegistryTests : IDisposable
{
    private readonly InMemoryPersonaKeyStore store = new();
    private readonly HandleBackupCodec codec = new();
    private readonly InMemoryRegistryStorage registry = new();
    private readonly PersonaBackupSecret secret = PersonaBackupSecret.FromText("correct horse battery staple");

    public void Dispose() => secret.Dispose();

    private PersonaManager Load() => PersonaManager.Load(store, codec, registry);

    [Fact]
    public void Load_RefusesNullSeams()
    {
        Assert.Throws<ArgumentNullException>(() => PersonaManager.Load(null!, codec, registry));
        Assert.Throws<ArgumentNullException>(() => PersonaManager.Load(store, null!, registry));
        Assert.Throws<ArgumentNullException>(() => PersonaManager.Load(store, codec, null!));
        Assert.Empty(registry.Calls);
    }

    [Fact]
    public void Load_WithNoRegistry_IsAFirstRun_AndSavesNothingUntilAChange()
    {
        var manager = Load();
        Assert.Empty(manager.Personas);
        Assert.Null(manager.Active);
        Assert.Equal(new[] { "Read" }, registry.Calls);
        Assert.Null(registry.Bytes);

        manager.Create("Main");
        Assert.Equal(new[] { "Read", "Replace" }, registry.Calls);
        AssertSavedMatches(manager);
    }

    [Fact]
    public void Load_BringsBackTheRecordsAndTheSelection_AsThePlayerLeftThem()
    {
        var first = Load();
        var main = first.Create("Main");
        var alt = first.Create("RP alt");
        first.Rename(main.Slot, "Main character");
        first.Acknowledge(alt.Slot);
        first.Select(alt.Slot);

        var second = Load();
        AssertSameRecords(first.Personas, second.Personas);
        Assert.Equal("Main character", second.Personas[0].Label);
        Assert.Equal(alt.Slot, second.Active!.Slot);
        Assert.True(second.Active.Acknowledged);

        // The loaded manager signs for the persona it brought back as active, and only for it.
        Assert.Equal(PersonaSignerAvailability.Available, second.TryOpenSigner(alt.Slot, alt.PublicKey, out var lease));
        using (lease)
        {
            Assert.Equal(alt.Id, SignedDocumentCodec.Verify(Documents.SignedRetraction(lease!.Signer)).Persona);
        }

        Assert.Equal(PersonaSignerAvailability.ActivePersonaChanged, second.TryOpenSigner(main.Slot, main.PublicKey, out _));
    }

    [Fact]
    public void Load_ThatCannotRead_IsRefused_AndTheRegistryIsNeverReplaced()
    {
        Load().Create("Main");
        var saved = registry.Bytes;
        registry.Calls.Clear();
        registry.ReadFailure = new IOException("The file is being used by another process.");

        var failure = Assert.Throws<PersonaException>(Load);
        Assert.Equal(PersonaError.RegistryUnreadable, failure.Error);
        Assert.IsType<IOException>(failure.InnerException);
        Assert.Equal(new[] { "Read" }, registry.Calls);
        Assert.Same(saved, registry.Bytes);
    }

    [Theory]
    [InlineData("damaged")]
    [InlineData("not a registry")]
    [InlineData("empty")]
    [InlineData("version zero")]
    public void Load_OfARegistryThisBuildDoesNotRead_IsRefused_AndLeftAsItWas(string kind)
    {
        Load().Create("Main");
        var bytes = registry.Bytes!;
        byte[] refused = kind switch
        {
            "damaged" => Flip(bytes, 30),
            "not a registry" => Enumerable.Repeat((byte)0x5A, 100).ToArray(),
            "empty" => [],
            _ => Rechecksummed(bytes, b => b[5] = 0),
        };
        registry.Bytes = refused;
        registry.Calls.Clear();

        var failure = Assert.Throws<PersonaException>(Load);
        Assert.Equal(PersonaError.RegistryUnreadable, failure.Error);
        Assert.DoesNotContain("Main", failure.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "Read" }, registry.Calls);
        Assert.Same(refused, registry.Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_OfARegistryANewerAetherFrameWrote_IsToldApart_AndLeftAsItWas(bool laterLayout)
    {
        // A later version may place its checksum elsewhere, so the version is read before the
        // checksum: either way the player is told to update, not that the file is damaged.
        Load().Create("Main");
        var newer = Rechecksummed(registry.Bytes!, b => b[5] = 2);
        if (laterLayout)
        {
            newer = Flip(newer, newer.Length - 1);
        }

        registry.Bytes = newer;
        registry.Calls.Clear();

        var failure = Assert.Throws<PersonaException>(Load);
        Assert.Equal(PersonaError.RegistryNewerVersion, failure.Error);
        Assert.Equal(new[] { "Read" }, registry.Calls);
        Assert.Same(newer, registry.Bytes);
    }

    [Fact]
    public void Create_WhenTheSaveFails_AddsNoRecord_AndItsKeyIsLeftForRestore()
    {
        var manager = Load();
        var main = manager.Create("Main");
        var saved = registry.Bytes;
        registry.FailNextReplace = new IOException("There is not enough space on the disk.");

        var failure = Assert.Throws<PersonaException>(() => manager.Create("Alt"));
        Assert.Equal(PersonaError.RegistryWriteFailed, failure.Error);
        Assert.IsType<IOException>(failure.InnerException);
        Assert.Equal(main.Slot, Assert.Single(manager.Personas).Slot);
        Assert.Same(saved, registry.Bytes);
        Assert.Equal(2, store.Count);

        // The committed key is an orphan the player can restore, under the slot it was committed to.
        var orphan = Assert.Single(manager.Audit().Orphans);
        Assert.NotEqual(main.Slot, orphan.Slot);
        Assert.Equal(PersonaKeyStatusOfOrphan.Readable, orphan.Status);
        var restored = manager.RestoreOrphan(orphan.Slot, "Alt");
        Assert.Equal(orphan.Slot, restored.Slot);
        Assert.Equal(orphan.ClaimedPublicKey, restored.PublicKey);
        Assert.Empty(manager.Audit().Orphans);
        AssertSavedMatches(manager);
    }

    [Fact]
    public void RestoreBackup_WhenTheSaveFails_AddsNoRecord_AndARetryCommitsAFreshSlot()
    {
        var backup = BackupHeldElsewhere(out var far);
        var manager = Load();
        registry.FailNextReplace = new IOException("There is not enough space on the disk.");

        Assert.Equal(PersonaError.RegistryWriteFailed, Assert.Throws<PersonaException>(() => manager.RestoreBackup(backup, secret, "Far")).Error);
        Assert.Empty(manager.Personas);
        var first = Assert.Single(manager.Audit().Orphans);
        Assert.Equal(far.PublicKey, first.ClaimedPublicKey);

        // A retry restores the persona under a fresh slot; the first copy stays an orphan, and
        // restoring it is refused because its identity is held now.
        var retried = manager.RestoreBackup(backup, secret, "Far");
        Assert.Equal(PersonaRestoreStatus.Restored, retried.Status);
        Assert.NotEqual(first.Slot, retried.Persona!.Slot);
        Assert.Equal(first.Slot, Assert.Single(manager.Audit().Orphans).Slot);
        Assert.Equal(PersonaError.NotAnOrphan, Assert.Throws<PersonaException>(() => manager.RestoreOrphan(first.Slot, "Far again")).Error);
        AssertSavedMatches(manager);
    }

    [Fact]
    public void Select_AndDeselect_WhenTheSaveFails_KeepTheSelection_AndEveryLeaseStillSigns()
    {
        var manager = Load();
        var main = manager.Create("Main");
        var alt = manager.Create("Alt");
        manager.Select(main.Slot);
        Assert.Equal(PersonaSignerAvailability.Available, manager.TryOpenActiveSigner(out var opened));
        using var lease = opened!;

        registry.FailNextReplace = new IOException("Access to the path is denied.");
        Assert.Equal(PersonaError.RegistryWriteFailed, Assert.Throws<PersonaException>(() => manager.Select(alt.Slot)).Error);
        Assert.Equal(main.Slot, manager.Active!.Slot);
        Documents.SignedRetraction(lease.Signer);

        registry.FailNextReplace = new IOException("Access to the path is denied.");
        Assert.Equal(PersonaError.RegistryWriteFailed, Assert.Throws<PersonaException>(manager.Deselect).Error);
        Assert.Equal(main.Slot, manager.Active!.Slot);
        Documents.SignedRetraction(lease.Signer);
        AssertSavedMatches(manager);

        // Once a switch is saved, it applies, and the lease stops signing.
        manager.Select(alt.Slot);
        Assert.Equal(PersonaError.LeaseRevoked, Assert.Throws<PersonaException>(() => Documents.SignedRetraction(lease.Signer)).Error);
        AssertSavedMatches(manager);
    }

    [Fact]
    public void Rename_AndAcknowledge_WhenTheSaveFails_ChangeNothing()
    {
        var manager = Load();
        var main = manager.Create("Main");

        registry.FailNextReplace = new IOException();
        Assert.Equal(PersonaError.RegistryWriteFailed, Assert.Throws<PersonaException>(() => manager.Rename(main.Slot, "Other")).Error);
        registry.FailNextReplace = new IOException();
        Assert.Equal(PersonaError.RegistryWriteFailed, Assert.Throws<PersonaException>(() => manager.Acknowledge(main.Slot)).Error);

        var record = Assert.Single(manager.Personas);
        Assert.Equal("Main", record.Label);
        Assert.False(record.Acknowledged);
        Assert.True(manager.TryGet(main.Slot, out var listed));
        Assert.Same(record, listed);
        AssertSavedMatches(manager);
    }

    [Fact]
    public void ASaveWhoseOutcomeIsUnknown_ChangesNothingInMemory_AndNoKeyIsLost()
    {
        // The storage held the new registry and then failed, so the manager cannot know which one it
        // holds. Memory keeps the state before the change; the registry holds the change; and the
        // key is never lost either way: recorded after a restart, an orphan until then.
        var manager = Load();
        var main = manager.Create("Main");
        registry.FailNextReplaceAfterHolding = new IOException("The flush failed.");

        Assert.Equal(PersonaError.RegistryWriteFailed, Assert.Throws<PersonaException>(() => manager.Create("Alt")).Error);
        Assert.Equal(main.Slot, Assert.Single(manager.Personas).Slot);
        var (saved, _) = Saved();
        Assert.Equal(2, saved.Count);

        var orphan = Assert.Single(manager.Audit().Orphans);
        Assert.Equal(saved[1].Slot, orphan.Slot);
        manager.RestoreOrphan(orphan.Slot, "Alt");
        AssertSavedMatches(manager);
    }

    [Fact]
    public void EveryChange_IsSavedOnce_AndWhatChangesNothingSavesNothing()
    {
        var backup = BackupHeldElsewhere(out _);
        var manager = Load();
        var main = manager.Create("Main");
        Assert.Equal(1, registry.Replacements);

        void Unchanged(Action action)
        {
            var before = registry.Replacements;
            try
            {
                action();
            }
            catch (PersonaException)
            {
            }

            Assert.Equal(before, registry.Replacements);
        }

        void SavedOnce(Action action)
        {
            var before = registry.Replacements;
            action();
            Assert.Equal(before + 1, registry.Replacements);
            AssertSavedMatches(manager);
        }

        SavedOnce(() => manager.Select(main.Slot));
        Unchanged(() => manager.Select(main.Slot));
        SavedOnce(() => manager.Acknowledge(main.Slot));
        Unchanged(() => manager.Acknowledge(main.Slot));
        SavedOnce(() => manager.Rename(main.Slot, "Main character"));
        Unchanged(() => manager.Rename(main.Slot, "  Main character  "));
        SavedOnce(manager.Deselect);
        Unchanged(manager.Deselect);
        SavedOnce(() => manager.RestoreBackup(backup, secret, "Far"));
        var orphan = PersonaSlotId.NewId();
        using (var material = PersonaKeyMaterial.Generate())
        {
            store.AddKey(orphan, material);
        }

        SavedOnce(() => manager.RestoreOrphan(orphan, "Found"));

        // Reads, and refusals, never save.
        Unchanged(() => _ = manager.Personas);
        Unchanged(() => _ = manager.Active);
        Unchanged(() => manager.TryGet(main.Slot, out _));
        Unchanged(() => manager.Audit());
        Unchanged(() => manager.VerifyOrphan(PersonaSlotId.NewId()));
        Unchanged(() => manager.TryOpenSigner(main.Slot, main.PublicKey, out _));
        Unchanged(() => manager.TryOpenActiveSigner(out _));
        Unchanged(() => manager.ExportBackup(main.Slot, secret));
        Unchanged(() => manager.InspectBackup(backup));
        Unchanged(() => manager.RestoreBackup(backup, secret, "Far again"));
        Unchanged(() => manager.Select(PersonaSlotId.NewId()));
        Unchanged(() => manager.Rename(main.Slot, " "));
        Unchanged(() => manager.Create("\u0000"));
        Unchanged(() => manager.RestoreOrphan(main.Slot, "Main"));
    }

    [Fact]
    public void TheRegistryHolds256Personas_AndTheNextIsRefusedBeforeAnyKeyIsCommitted()
    {
        var manager = Load();
        for (var index = 0; index < PersonaManager.MaxPersonas; index++)
        {
            manager.Create("Persona " + index);
        }

        Assert.Equal(PersonaManager.MaxPersonas, Saved().Records.Count);
        var backup = BackupHeldElsewhere(out _);
        var orphan = PersonaSlotId.NewId();
        using (var material = PersonaKeyMaterial.Generate())
        {
            store.AddKey(orphan, material);
        }

        var generated = store.CallsTo("GenerateKey");
        var committed = store.CallsTo("AddKey");
        var saves = registry.Replacements;

        Assert.Equal(PersonaError.RegistryFull, Assert.Throws<PersonaException>(() => manager.Create("One more")).Error);
        Assert.Equal(PersonaError.RegistryFull, Assert.Throws<PersonaException>(() => manager.RestoreBackup(backup, secret, "Far")).Error);
        Assert.Equal(PersonaError.RegistryFull, Assert.Throws<PersonaException>(() => manager.RestoreOrphan(orphan, "Found")).Error);

        Assert.Equal(generated, store.CallsTo("GenerateKey"));
        Assert.Equal(committed, store.CallsTo("AddKey"));
        Assert.Equal(saves, registry.Replacements);
        Assert.Equal(PersonaManager.MaxPersonas, manager.Personas.Count);

        // A persona already held is still reported as present, full or not.
        var mine = manager.ExportBackup(manager.Personas[0].Slot, secret);
        Assert.Equal(PersonaRestoreStatus.AlreadyPresent, manager.RestoreBackup(mine, secret, "Again").Status);
    }

    [Fact]
    public void K4_StartsUnset_IsSaved_SurvivesARename_AndARestoreStartsWithoutIt()
    {
        var manager = Load();
        var main = manager.Create("Main");
        Assert.False(main.Acknowledged);

        Assert.True(manager.Acknowledge(main.Slot).Acknowledged);
        Assert.True(manager.Rename(main.Slot, "Renamed").Acknowledged);
        Assert.True(Assert.Single(Saved().Records).Acknowledged);
        Assert.Equal(PersonaError.UnknownPersona, Assert.Throws<PersonaException>(() => manager.Acknowledge(PersonaSlotId.NewId())).Error);

        // A backup carries the key and nothing the player acknowledged: restored elsewhere, the
        // persona asks again.
        var backup = manager.ExportBackup(main.Slot, secret);
        var elsewhere = PersonaManager.Load(new InMemoryPersonaKeyStore(), codec, new InMemoryRegistryStorage());
        var restored = elsewhere.RestoreBackup(backup, secret, "Main");
        Assert.Equal(PersonaRestoreStatus.Restored, restored.Status);
        Assert.False(restored.Persona!.Acknowledged);
    }

    [Fact]
    public void TryOpenSigner_SignsOnlyForThePersonaTheOperationShowed()
    {
        var manager = Load();
        var main = manager.Create("Main");
        var alt = manager.Create("Alt");

        Assert.Equal(PersonaSignerAvailability.ActivePersonaChanged, manager.TryOpenSigner(main.Slot, main.PublicKey, out var none));
        Assert.Null(none);

        manager.Select(main.Slot);
        var opened = store.CallsTo("OpenSigner");
        Assert.Equal(PersonaSignerAvailability.ActivePersonaChanged, manager.TryOpenSigner(alt.Slot, alt.PublicKey, out none));
        Assert.Equal(PersonaSignerAvailability.ActivePersonaChanged, manager.TryOpenSigner(main.Slot, alt.PublicKey, out none));
        Assert.Equal(PersonaSignerAvailability.ActivePersonaChanged, manager.TryOpenSigner(alt.Slot, main.PublicKey, out none));
        Assert.Equal(PersonaSignerAvailability.ActivePersonaChanged, manager.TryOpenSigner(default, main.PublicKey, out none));
        Assert.Null(none);
        Assert.Equal(opened, store.CallsTo("OpenSigner"));
        Assert.Throws<ArgumentNullException>(() => manager.TryOpenSigner(main.Slot, null!, out _));

        Assert.Equal(PersonaSignerAvailability.Available, manager.TryOpenSigner(main.Slot, main.PublicKey, out var lease));
        using (lease)
        {
            Assert.Equal(main.Id, SignedDocumentCodec.Verify(Documents.SignedRetraction(lease!.Signer)).Persona);

            // A switch after the lease opened revokes it, as it does every lease.
            manager.Select(alt.Slot);
            Assert.Equal(PersonaError.LeaseRevoked, Assert.Throws<PersonaException>(() => Documents.SignedRetraction(lease.Signer)).Error);
        }

        store.Lock(alt.Slot);
        Assert.Equal(PersonaSignerAvailability.KeyUnavailable, manager.TryOpenSigner(alt.Slot, alt.PublicKey, out none));
        Assert.Null(none);
    }

    [Fact]
    public async Task Listings_NeverWaitOnASave_AndShowTheStateBeforeIt()
    {
        var manager = Load();
        var main = manager.Create("Main");
        var alt = manager.Create("Alt");
        manager.Select(main.Slot);

        using var release = new ManualResetEventSlim(false);
        registry.HoldNextReplace = release;
        var switching = Task.Run(() => manager.Select(alt.Slot));
        try
        {
            Assert.True(registry.Entered.Wait(TimeSpan.FromSeconds(10)));

            // The switch holds the lock inside its save. Every listing answers at once from the
            // snapshot, which still shows the state before the switch.
            var listing = Task.Run(() =>
            {
                Assert.Equal(main.Slot, manager.Active!.Slot);
                Assert.Equal(2, manager.Personas.Count);
                Assert.True(manager.TryGet(alt.Slot, out var found));
                Assert.Equal("Alt", found!.Label);
            });
            Assert.Same(listing, await Task.WhenAny(listing, Task.Delay(TimeSpan.FromSeconds(10))));
            await listing;
            Assert.False(switching.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(alt.Slot, (await switching).Slot);
        Assert.Equal(alt.Slot, manager.Active!.Slot);
        AssertSavedMatches(manager);
    }

    [Fact]
    public void Personas_IsACopy_ThatAChangeLeavesAsItWas()
    {
        var manager = Load();
        manager.Create("Main");
        var before = manager.Personas;
        manager.Create("Alt");
        Assert.Single(before);
        Assert.Equal(2, manager.Personas.Count);
        Assert.NotSame(manager.Personas, manager.Personas);
    }

    private byte[] BackupHeldElsewhere(out PersonaRecord persona)
    {
        var elsewhere = new PersonaManager(new InMemoryPersonaKeyStore(), codec);
        persona = elsewhere.Create("Far");
        return elsewhere.ExportBackup(persona.Slot, secret);
    }

    /// <summary>The records and the selection the registry holds now.</summary>
    private (IReadOnlyList<PersonaRecord> Records, PersonaSlotId Active) Saved()
    {
        Assert.NotNull(registry.Bytes);
        Assert.True(PersonaRegistryCodec.TryDecode(registry.Bytes, out var records, out var active, out var reason), reason);
        return (records, active);
    }

    /// <summary>The registry holds exactly what the manager shows.</summary>
    private void AssertSavedMatches(PersonaManager manager)
    {
        var (records, active) = Saved();
        AssertSameRecords(manager.Personas, records);
        Assert.Equal(manager.Active?.Slot ?? default, active);
    }

    private static void AssertSameRecords(IReadOnlyList<PersonaRecord> expected, IReadOnlyList<PersonaRecord> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Slot, actual[index].Slot);
            Assert.Equal(expected[index].PublicKey, actual[index].PublicKey);
            Assert.Equal(expected[index].Label, actual[index].Label, StringComparer.Ordinal);
            Assert.Equal(expected[index].Acknowledged, actual[index].Acknowledged);
        }
    }

    private static byte[] Flip(byte[] bytes, int index)
    {
        var copy = (byte[])bytes.Clone();
        copy[index] ^= 0x01;
        return copy;
    }

    /// <summary>A copy changed by <paramref name="change"/>, with its checksum made right again.</summary>
    private static byte[] Rechecksummed(byte[] bytes, Action<byte[]> change)
    {
        var copy = (byte[])bytes.Clone();
        change(copy);
        System.Security.Cryptography.SHA256.HashData(copy.AsSpan(0, copy.Length - 32), copy.AsSpan(copy.Length - 32));
        return copy;
    }
}
