using System;
using System.IO;
using System.Linq;
using AetherFrame.Personas.Storage;
using AetherFrame.Protocol.Identity;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// Decision L12 over the real key store core: the audit compares what the key storage holds with
/// the records without opening a key, and reports orphans (keys no record names) and unusable
/// records, each with its reason; an orphan's identity is a claim until it is verified by opening
/// the key; and only the player restores one, after everything is checked again. Nothing is ever
/// repaired or deleted. The storage is a dictionary, the protector protects nothing, and the
/// registry is in memory.
/// </summary>
public sealed class PersonaOrphanTests
{
    private readonly InMemoryKeyBlobStorage storage = new();
    private readonly FakeProtector protector = new();
    private readonly InMemoryRegistryStorage registry = new();
    private readonly ProtectedPersonaKeyStore store;

    public PersonaOrphanTests()
    {
        store = new ProtectedPersonaKeyStore(storage, protector, _ => { });
    }

    private PersonaManager Load() => PersonaManager.Load(store, new HandleBackupCodec(), registry);

    [Fact]
    public void Audit_OfAnInstallationInOrder_FindsNothing()
    {
        var manager = Load();
        manager.Create("Main");
        manager.Create("Alt");

        var audit = manager.Audit();
        Assert.Empty(audit.Orphans);
        Assert.Empty(audit.Unusable);
        Assert.False(audit.ListingFailed);
        Assert.Equal(0, audit.SkippedEntries);
    }

    [Fact]
    public void Audit_FindsEveryOrphan_WithWhatItsHeaderClaims_WithoutOpeningAnyKey()
    {
        var manager = Load();
        var main = manager.Create("Main");
        var readable = Orphan(out var readableKey);
        var garbage = PersonaSlotId.NewId();
        storage.Plant(garbage, [1, 2, 3]);
        var copied = PersonaSlotId.NewId();
        storage.Plant(copied, storage.Held(main.Slot));
        var foreign = PersonaSlotId.NewId();
        using (var material = PersonaKeyMaterial.Generate())
        {
            new ProtectedPersonaKeyStore(storage, new FakeProtector("test.other.v1")).AddKey(foreign, material);
        }

        storage.SkippedEntries = 2;
        protector.Calls.Clear();

        var audit = manager.Audit();
        Assert.Empty(protector.Calls);
        Assert.Empty(audit.Unusable);
        Assert.Equal(2, audit.SkippedEntries);
        var orphans = audit.Orphans.ToDictionary(o => o.Slot);
        Assert.Equal(4, orphans.Count);

        Assert.Equal(PersonaKeyStatusOfOrphan.Readable, orphans[readable].Status);
        Assert.Equal(readableKey, orphans[readable].ClaimedPublicKey);
        Assert.Equal(readableKey.Id, orphans[readable].ClaimedId);
        Assert.Equal(readable.ToString(), orphans[readable].ToString());

        Assert.Equal(PersonaKeyStatusOfOrphan.Unreadable, orphans[garbage].Status);
        Assert.Equal(PersonaKeyStatusOfOrphan.NamesAnotherSlot, orphans[copied].Status);
        Assert.Equal(PersonaKeyStatusOfOrphan.Unreadable, orphans[foreign].Status);
        foreach (var slot in new[] { garbage, copied, foreign })
        {
            Assert.Null(orphans[slot].ClaimedPublicKey);
            Assert.Null(orphans[slot].ClaimedId);
        }
    }

    [Fact]
    public void Audit_FindsEveryUnusableRecord_SaysWhy_AndRepairsNothing()
    {
        var manager = Load();
        var missing = manager.Create("Missing");
        var unreadable = manager.Create("Unreadable");
        var moved = manager.Create("Moved");
        var swapped = manager.Create("Swapped");
        var fine = manager.Create("Fine");

        storage.Remove(missing.Slot);
        storage.Plant(unreadable.Slot, [9, 9, 9]);
        storage.Plant(moved.Slot, storage.Held(fine.Slot));
        storage.Plant(swapped.Slot, WithClaimedKey(swapped.Slot, fine.PublicKey));

        var audit = manager.Audit();
        Assert.Empty(audit.Orphans);
        Assert.Equal(
            new[]
            {
                (missing.Slot, PersonaUnusableReason.KeyMissing),
                (unreadable.Slot, PersonaUnusableReason.KeyUnreadable),
                (moved.Slot, PersonaUnusableReason.KeyNamesAnotherSlot),
                (swapped.Slot, PersonaUnusableReason.KeyNamesAnotherKey),
            },
            audit.Unusable.Select(u => (u.Record.Slot, u.Reason)));
        Assert.Equal(missing.Slot + ": KeyMissing", audit.Unusable[0].ToString());
        Assert.Equal(5, manager.Personas.Count);
    }

    [Fact]
    public void Audit_WhenTheListingFails_SaysSo_AndStillChecksEveryRecord()
    {
        var manager = Load();
        var main = manager.Create("Main");
        Orphan(out _);
        storage.Remove(main.Slot);
        storage.ListFailure = new IOException("Access to the path is denied.");

        var audit = manager.Audit();
        Assert.True(audit.ListingFailed);
        Assert.Empty(audit.Orphans);
        Assert.Equal(PersonaUnusableReason.KeyMissing, Assert.Single(audit.Unusable).Reason);
    }

    [Fact]
    public void Audit_WhenTheStorageCannotReadAKey_CallsItUnreadable()
    {
        var manager = Load();
        manager.Create("Main");
        Orphan(out _);
        storage.ReadFailure = new IOException("The file is being used by another process.");

        var audit = manager.Audit();
        Assert.Equal(PersonaUnusableReason.KeyUnreadable, Assert.Single(audit.Unusable).Reason);
        Assert.Equal(PersonaKeyStatusOfOrphan.Unreadable, Assert.Single(audit.Orphans).Status);
    }

    [Fact]
    public void VerifyOrphan_ProvesTheKeyByOpeningIt_OrReturnsNull()
    {
        var manager = Load();
        var main = manager.Create("Main");
        var orphan = Orphan(out var publicKey);
        Assert.Equal(publicKey, manager.VerifyOrphan(orphan));

        Assert.Null(manager.VerifyOrphan(main.Slot));
        Assert.Null(manager.VerifyOrphan(default));
        Assert.Null(manager.VerifyOrphan(PersonaSlotId.NewId()));
        protector.Locked = true;
        Assert.Null(manager.VerifyOrphan(orphan));
        protector.Locked = false;

        // A header that claims another key: the audit shows the claim, and opening refutes it.
        storage.Plant(orphan, WithClaimedKey(orphan, main.PublicKey));
        Assert.Equal(main.PublicKey, Assert.Single(manager.Audit().Orphans).ClaimedPublicKey);
        Assert.Null(manager.VerifyOrphan(orphan));
    }

    [Fact]
    public void RestoreOrphan_KeepsTheSlot_TakesTheKeyOpenedNow_AndSavesBeforeApplying()
    {
        var manager = Load();
        var main = manager.Create("Main");
        manager.Select(main.Slot);
        var orphan = Orphan(out var publicKey);

        registry.FailNextReplace = new IOException("There is not enough space on the disk.");
        Assert.Equal(PersonaError.RegistryWriteFailed, Assert.Throws<PersonaException>(() => manager.RestoreOrphan(orphan, "Found")).Error);
        Assert.Single(manager.Personas);
        Assert.Equal(orphan, Assert.Single(manager.Audit().Orphans).Slot);

        var restored = manager.RestoreOrphan(orphan, "  Found  ");
        Assert.Equal(orphan, restored.Slot);
        Assert.Equal(publicKey, restored.PublicKey);
        Assert.Equal("Found", restored.Label);
        Assert.False(restored.Acknowledged);
        Assert.Equal(main.Slot, manager.Active!.Slot);
        Assert.Empty(manager.Audit().Orphans);
        Assert.True(PersonaRegistryCodec.TryDecode(registry.Bytes, out var records, out var active, out _));
        Assert.Equal(new[] { main.Slot, orphan }, records.Select(r => r.Slot));
        Assert.Equal(main.Slot, active);

        // The restored persona signs once the player selects it.
        manager.Select(orphan);
        Assert.Equal(PersonaSignerAvailability.Available, manager.TryOpenSigner(orphan, publicKey, out var lease));
        lease!.Dispose();
    }

    [Fact]
    public void RestoreOrphan_RefusesWhatIsNotAnOrphan_AndChangesNothing()
    {
        var manager = Load();
        var main = manager.Create("Main");
        var orphan = Orphan(out _);
        var moved = PersonaSlotId.NewId();
        storage.Plant(moved, storage.Held(orphan));
        var copy = PersonaSlotId.NewId();
        using (var material = store.OpenKey(main.Slot))
        {
            store.AddKey(copy, material!);
        }

        var saves = registry.Replacements;
        Assert.Equal(PersonaError.NotAnOrphan, Refusal(() => manager.RestoreOrphan(main.Slot, "Again")));
        Assert.Equal(PersonaError.NotAnOrphan, Refusal(() => manager.RestoreOrphan(default, "Empty")));
        Assert.Equal(PersonaError.NotAnOrphan, Refusal(() => manager.RestoreOrphan(PersonaSlotId.NewId(), "Nothing")));
        Assert.Equal(PersonaError.NotAnOrphan, Refusal(() => manager.RestoreOrphan(moved, "Moved")));
        Assert.Equal(PersonaError.NotAnOrphan, Refusal(() => manager.RestoreOrphan(copy, "Copy")));
        Assert.Equal(PersonaError.InvalidLabel, Refusal(() => manager.RestoreOrphan(orphan, " ")));
        protector.Locked = true;
        Assert.Equal(PersonaError.NotAnOrphan, Refusal(() => manager.RestoreOrphan(orphan, "Locked")));
        protector.Locked = false;

        Assert.Equal(saves, registry.Replacements);
        Assert.Equal(main.Slot, Assert.Single(manager.Personas).Slot);
        Assert.Equal(orphan, manager.RestoreOrphan(orphan, "Found").Slot);
    }

    private static PersonaError Refusal(Action action) => Assert.Throws<PersonaException>(action).Error;

    /// <summary>A key committed under a new slot no record names: what a registry save that failed after the commit leaves.</summary>
    private PersonaSlotId Orphan(out PersonaPublicKey publicKey)
    {
        var slot = PersonaSlotId.NewId();
        using var material = store.GenerateKey();
        store.AddKey(slot, material);
        publicKey = material.PublicKey;
        return slot;
    }

    /// <summary>The slot's own envelope with its header claiming <paramref name="claimed"/> instead: a forged claim.</summary>
    private byte[] WithClaimedKey(PersonaSlotId slot, PersonaPublicKey claimed)
    {
        Assert.True(ProtectedKeyEnvelope.TryDecode(storage.Held(slot), out var decoded));
        return ProtectedKeyEnvelope.Encode(ProtectedKeyEnvelope.EncodeHeader(slot, FakeProtector.DefaultId, claimed), decoded.Blob);
    }
}
