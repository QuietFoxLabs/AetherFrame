using System;
using System.Linq;
using AetherFrame.Personas.Storage;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Signing;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The key store core against the store contract (docs/networking/NETWORK1_PersonaFoundation.md,
/// section 4): it commits exactly the key it was given and proves that before anything is durable,
/// never replaces, is atomic, never retains the caller's material, and reports every unavailable
/// key as null. The storage is a dictionary and the protector protects nothing; both are told to
/// misbehave in every way a platform might.
/// </summary>
public class ProtectedPersonaKeyStoreTests
{
    private readonly InMemoryKeyBlobStorage storage = new();
    private readonly FakeProtector protector = new();
    private readonly System.Collections.Generic.List<string> reports = new();

    private ProtectedPersonaKeyStore NewStore() => new(storage, protector, reports.Add);

    private static PersonaSlotId NewSlot() => PersonaSlotId.NewId();

    [Fact]
    public void AKeyRoundTrips_ThroughTheEnvelopeAndTheProtector()
    {
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        store.AddKey(slot, material);

        using var opened = store.OpenKey(slot);
        Assert.NotNull(opened);
        Assert.Equal(material.PublicKey, opened!.PublicKey);
        Assert.NotSame(material, opened);

        using var signer = Assert.IsType<EcdsaPersonaSigner>(store.OpenSigner(slot));
        Assert.Equal(material.PublicKey, signer.PublicKey);
        var document = Documents.SignedRetraction(signer);
        Assert.NotNull(SignedDocumentCodec.Verify(document));
        Assert.Empty(reports);
    }

    [Fact]
    public void GenerateKey_MakesAFreshKeyEveryTime_AndHoldsNothing()
    {
        var store = NewStore();
        using var first = store.GenerateKey();
        using var second = store.GenerateKey();
        Assert.NotEqual(first.PublicKey, second.PublicKey);
        Assert.Equal(0, storage.Count);
        Assert.Empty(storage.Calls);
    }

    [Fact]
    public void AddKey_WritesOneEnvelope_ProvedBeforeItIsDurable_AndReadBackAfter()
    {
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        store.AddKey(slot, material);

        Assert.Equal(["Read", "WriteNew", "Read"], storage.Calls);
        Assert.Equal(["Protect", "Unprotect"], protector.Calls);
        Assert.Equal(1, storage.Count);
        Assert.True(ProtectedKeyEnvelope.TryDecode(storage.Held(slot), out var decoded));
        Assert.Equal(slot, decoded.Slot);
        Assert.Equal(FakeProtector.DefaultId, decoded.ProtectorId);
        Assert.Equal(material.PublicKey, decoded.PublicKey);
    }

    [Fact]
    public void AddKey_NeverRetainsTheCallersMaterial()
    {
        var store = NewStore();
        var slot = NewSlot();
        var material = store.GenerateKey();
        store.AddKey(slot, material);
        material.Dispose();

        using var opened = store.OpenKey(slot);
        Assert.NotNull(opened);
        Assert.Equal(material.PublicKey, opened!.PublicKey);
    }

    [Fact]
    public void ThePlainScalarIsNotInTheEnvelope_AndThePublicKeyIs()
    {
        var store = NewStore();
        var slot = NewSlot();
        using var key = SyntheticKeys.Create();
        using var material = PersonaKeyMaterial.FromEcdsa(key);
        store.AddKey(slot, material);

        var held = Convert.ToHexStringLower(storage.Held(slot));
        Assert.DoesNotContain(SyntheticKeys.PrivateHex(key), held, StringComparison.Ordinal);
        Assert.Contains(SyntheticKeys.PublicHex(material.PublicKey), held, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexStringLower(System.Text.Encoding.ASCII.GetBytes(material.PublicKey.Id.ToString())), held, StringComparison.Ordinal);
    }

    [Fact]
    public void AddKey_ZeroesEveryScalarTheProtectorHandedBack()
    {
        var store = NewStore();
        using var material = store.GenerateKey();
        store.AddKey(NewSlot(), material);
        var handed = Assert.Single(protector.HandedOut);
        Assert.All(handed, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Open_ZeroesTheScalarTheProtectorHandedBack()
    {
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        store.AddKey(slot, material);
        protector.HandedOut.Clear();

        using var opened = store.OpenKey(slot);
        using var signer = store.OpenSigner(slot) as IDisposable;
        Assert.Equal(2, protector.HandedOut.Count);
        Assert.All(protector.HandedOut, array => Assert.All(array, b => Assert.Equal(0, b)));
    }

    [Fact]
    public void AddKey_RefusesAHeldSlot_AndLeavesItAsItWas()
    {
        var store = NewStore();
        var slot = NewSlot();
        using var first = store.GenerateKey();
        using var second = store.GenerateKey();
        store.AddKey(slot, first);
        var before = storage.Held(slot);

        Assert.Throws<InvalidOperationException>(() => store.AddKey(slot, second));
        Assert.Equal(before, storage.Held(slot));
        Assert.Equal(1, storage.Count);
        using var opened = store.OpenKey(slot);
        Assert.Equal(first.PublicKey, opened!.PublicKey);
    }

    [Fact]
    public void AddKey_RefusesTheEmptySlotAndNullMaterial()
    {
        var store = NewStore();
        using var material = store.GenerateKey();
        Assert.Throws<ArgumentException>(() => store.AddKey(default, material));
        Assert.Throws<ArgumentNullException>(() => store.AddKey(NewSlot(), null!));
        Assert.Equal(0, storage.Count);
    }

    [Fact]
    public void AddKey_WhenTheProtectorThrows_HoldsNothing()
    {
        var store = NewStore();
        using var material = store.GenerateKey();
        protector.FailNextProtect = new InvalidOperationException("platform");

        var failure = Assert.Throws<PersonaException>(() => store.AddKey(NewSlot(), material));
        Assert.Equal(PersonaError.CustodyFailed, failure.Error);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Equal(0, storage.Count);
        Assert.DoesNotContain("WriteNew", storage.Calls);
    }

    [Fact]
    public void AddKey_WhenTheProtectorProducesAnUnusableBlob_HoldsNothing()
    {
        var store = NewStore();
        using var material = store.GenerateKey();

        protector.NextBlob = [];
        Assert.Equal(PersonaError.CustodyFailed, Assert.Throws<PersonaException>(() => store.AddKey(NewSlot(), material)).Error);

        protector.NextBlob = new byte[ProtectedKeyEnvelope.MaxBlobLength + 1];
        Assert.Equal(PersonaError.CustodyFailed, Assert.Throws<PersonaException>(() => store.AddKey(NewSlot(), material)).Error);

        // A blob the protector cannot open again: garbage under the right length.
        protector.NextBlob = new byte[48];
        Assert.Equal(PersonaError.CustodyFailed, Assert.Throws<PersonaException>(() => store.AddKey(NewSlot(), material)).Error);

        Assert.Equal(0, storage.Count);
        Assert.DoesNotContain("WriteNew", storage.Calls);
    }

    [Fact]
    public void AddKey_WhenTheProtectorLiesAboutTheScalar_HoldsNothing()
    {
        // The protector opens its own blob to a different (valid) scalar: the envelope would hold a
        // key that is not the persona's. The proof before the write catches it.
        var store = NewStore();
        using var material = store.GenerateKey();
        using var other = SyntheticKeys.Create();
        protector.SubstituteNextUnprotect = SyntheticKeys.Scalar(other);

        var failure = Assert.Throws<PersonaException>(() => store.AddKey(NewSlot(), material));
        Assert.Equal(PersonaError.CustodyFailed, failure.Error);
        Assert.Equal(0, storage.Count);
        Assert.DoesNotContain("WriteNew", storage.Calls);
    }

    [Fact]
    public void AddKey_WhenTheStorageThrows_HoldsNothing_AndReportsTheCause()
    {
        var store = NewStore();
        using var material = store.GenerateKey();
        storage.FailNextWrite = new System.IO.IOException("disk");

        var failure = Assert.Throws<PersonaException>(() => store.AddKey(NewSlot(), material));
        Assert.Equal(PersonaError.CustodyFailed, failure.Error);
        Assert.IsType<System.IO.IOException>(failure.InnerException);
        Assert.Equal(0, storage.Count);
    }

    [Fact]
    public void AddKey_WhenTheStorageHoldsSomethingElse_ReportsIt_AndWhatItHoldsStays()
    {
        // A storage that breaks its contract (holds different bytes) is not hidden: the read-back
        // fails at once, with no retry, and the caller learns the key is not in custody. The store
        // cannot delete (K6), so what the storage holds stays under the slot, which nobody records (L12).
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        storage.SubstituteNextWrite = bytes => { bytes[^1] ^= 0x01; return bytes; };

        var failure = Assert.Throws<PersonaException>(() => store.AddKey(slot, material));
        Assert.Equal(PersonaError.CustodyFailed, failure.Error);
        Assert.Null(failure.InnerException);
        Assert.Contains("may stay", failure.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "Read", "WriteNew", "Read" }, storage.Calls);
        Assert.Equal(slot, Assert.Single(storage.Slots));
    }

    [Fact]
    public void AddKey_WhenTheStorageCannotBeRead_HoldsNothing()
    {
        var store = NewStore();
        using var material = store.GenerateKey();
        storage.ReadFailure = new System.IO.IOException("locked");

        var failure = Assert.Throws<PersonaException>(() => store.AddKey(NewSlot(), material));
        Assert.Equal(PersonaError.CustodyFailed, failure.Error);
        Assert.Equal(0, storage.Count);
        Assert.DoesNotContain("WriteNew", storage.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AddKey_RetriesAReadBackThatFailsAfterTheWrite_AndHoldsTheKey(int failures)
    {
        // A file a scanner opened just after it was written refuses a read for a moment: the key is
        // held, so the store tries the read-back again rather than report a key it cannot undo.
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        storage.FailReadsAfterNextWrite = failures;

        store.AddKey(slot, material);
        Assert.Equal(1 + 1 + failures + 1, storage.Calls.Count);
        using var opened = store.OpenKey(slot);
        Assert.Equal(material.PublicKey, opened!.PublicKey);
        Assert.Empty(reports);
    }

    [Fact]
    public void AddKey_WhenTheReadBackKeepsFailingAfterTheWrite_ReportsIt_AndTheEnvelopeStays()
    {
        // The one case the store cannot undo (L12): the storage accepted the envelope, then every
        // read-back failed. The caller gets CustodyFailed and never records the slot; the envelope
        // stays under it, a key file no record names, which the wiring must detect and report.
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        storage.FailReadsAfterNextWrite = 3;

        var failure = Assert.Throws<PersonaException>(() => store.AddKey(slot, material));
        Assert.Equal(PersonaError.CustodyFailed, failure.Error);
        Assert.IsType<System.IO.IOException>(failure.InnerException);
        Assert.Contains("may stay", failure.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "Read", "WriteNew", "Read", "Read", "Read" }, storage.Calls);
        Assert.Equal(slot, Assert.Single(storage.Slots));
        Assert.All(protector.HandedOut, array => Assert.All(array, b => Assert.Equal(0, b)));
    }

    [Fact]
    public void AddKey_WhenTheStorageThrowsAfterHolding_ReportsIt_AndTheEnvelopeStays()
    {
        // A storage that breaks its own atomicity: the store reports the failure as it must, and
        // cannot take back what the storage kept (L12).
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        storage.FailNextWriteAfterHolding = new System.IO.IOException("late");

        var failure = Assert.Throws<PersonaException>(() => store.AddKey(slot, material));
        Assert.Equal(PersonaError.CustodyFailed, failure.Error);
        Assert.Equal(slot, Assert.Single(storage.Slots));
    }

    [Fact]
    public void TheManager_AddsNoRecord_WhenTheCommitFailsAfterTheWrite()
    {
        var store = NewStore();
        var manager = new PersonaManager(store, new HandleBackupCodec());
        storage.FailReadsAfterNextWrite = 3;

        var failure = Assert.Throws<PersonaException>(() => manager.Create("Main"));
        Assert.Equal(PersonaError.CustodyFailed, failure.Error);
        Assert.Empty(manager.Personas);
        Assert.Equal(1, storage.Count);

        // The next persona gets a fresh slot, never the unrecorded one.
        var next = manager.Create("Main");
        Assert.Equal(2, storage.Count);
        Assert.Equal(next.Slot, Assert.Single(manager.Personas).Slot);
    }

    [Fact]
    public void Open_ReturnsNullForASlotNobodyHolds_AndSaysSo()
    {
        var store = NewStore();
        var slot = NewSlot();
        Assert.Null(store.OpenKey(slot));
        Assert.Null(store.OpenSigner(slot));
        Assert.Null(store.OpenKey(default));
        Assert.Equal(3, reports.Count);
        Assert.All(reports, r => Assert.Contains("unavailable", r, StringComparison.Ordinal));
        Assert.Contains(slot.ToString(), reports[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Open_ReturnsNullWhenTheProtectorCannotOpenTheKey()
    {
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        store.AddKey(slot, material);

        protector.Locked = true;
        Assert.Null(store.OpenKey(slot));
        Assert.Null(store.OpenSigner(slot));
        protector.Locked = false;

        protector.UnprotectFailure = new InvalidOperationException("platform");
        Assert.Null(store.OpenKey(slot));
        protector.UnprotectFailure = null;

        using var opened = store.OpenKey(slot);
        Assert.NotNull(opened);
        Assert.Equal(3, reports.Count);
    }

    [Fact]
    public void Open_ReturnsNullForAnEnvelopeOfAnotherProtector()
    {
        var slot = NewSlot();
        using var material = PersonaKeyMaterial.Generate();
        new ProtectedPersonaKeyStore(storage, new FakeProtector("test.other.v1")).AddKey(slot, material);

        Assert.Null(NewStore().OpenKey(slot));
        Assert.Contains("another protector", Assert.Single(reports), StringComparison.Ordinal);
    }

    [Fact]
    public void Open_ReturnsNullForDamage_WhereverItIs()
    {
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        store.AddKey(slot, material);
        var good = storage.Held(slot);
        var blobStart = good.Length - 48;

        // A flipped bit in the blob: the protector refuses it.
        var blob = (byte[])good.Clone();
        blob[blobStart + 20] ^= 0x01;
        storage.Plant(slot, blob);
        Assert.Null(store.OpenKey(slot));

        // A flipped bit in the public key that keeps it on the curve is astronomically unlikely, so
        // this one leaves the curve: the envelope does not decode.
        var publicKey = (byte[])good.Clone();
        publicKey[blobStart - 10] ^= 0x01;
        storage.Plant(slot, publicKey);
        Assert.Null(store.OpenKey(slot));

        // Truncated, garbage, empty.
        storage.Plant(slot, good[..^1]);
        Assert.Null(store.OpenKey(slot));
        storage.Plant(slot, new byte[good.Length]);
        Assert.Null(store.OpenKey(slot));
        storage.Plant(slot, []);
        Assert.Null(store.OpenKey(slot));

        // The original still opens.
        storage.Plant(slot, good);
        using var opened = store.OpenKey(slot);
        Assert.NotNull(opened);
        Assert.Equal(5, reports.Count);
    }

    [Fact]
    public void Open_ReturnsNullForABlobMovedBetweenSlots_OrUnderAnotherPublicKey()
    {
        var store = NewStore();
        var first = NewSlot();
        var second = NewSlot();
        using var firstKey = store.GenerateKey();
        using var secondKey = store.GenerateKey();
        store.AddKey(first, firstKey);
        store.AddKey(second, secondKey);

        // Both envelopes are decoded before anything is planted, so each case below starts from
        // what the store itself wrote.
        var secondEnvelope = storage.Held(second);
        Assert.True(ProtectedKeyEnvelope.TryDecode(storage.Held(first), out var firstDecoded));
        Assert.True(ProtectedKeyEnvelope.TryDecode(secondEnvelope, out var secondDecoded));
        Assert.Equal(second, secondDecoded.Slot);
        Assert.Equal(secondKey.PublicKey, secondDecoded.PublicKey);

        // The whole envelope of the first under the second's slot: names another slot.
        storage.Plant(second, storage.Held(first));
        Assert.Null(store.OpenKey(second));
        Assert.Contains("another slot", reports.Last(), StringComparison.Ordinal);

        // The first's blob inside the second's header: the context differs, so it does not open.
        storage.Plant(second, ProtectedKeyEnvelope.Encode(secondDecoded.Context, firstDecoded.Blob));
        Assert.Null(store.OpenKey(second));
        Assert.Contains("could not open", reports.Last(), StringComparison.Ordinal);

        // The second's own blob under the first's public key: the protector's context differs too.
        var header = ProtectedKeyEnvelope.EncodeHeader(second, FakeProtector.DefaultId, firstKey.PublicKey);
        storage.Plant(second, ProtectedKeyEnvelope.Encode(header, secondDecoded.Blob));
        Assert.Null(store.OpenKey(second));
        Assert.Contains("could not open", reports.Last(), StringComparison.Ordinal);

        // The second's own envelope still opens, so each refusal above came from the move.
        storage.Plant(second, secondEnvelope);
        using var opened = store.OpenKey(second);
        Assert.Equal(secondKey.PublicKey, opened!.PublicKey);
    }

    [Fact]
    public void Open_ReturnsNull_EvenWhenTheReportSinkThrows()
    {
        var store = new ProtectedPersonaKeyStore(storage, protector, _ => throw new InvalidOperationException("log"));
        Assert.Null(store.OpenKey(NewSlot()));
        Assert.Null(store.OpenSigner(NewSlot()));
    }

    [Fact]
    public void Open_ReturnsNullWhenTheScalarDoesNotBelongToTheRecordedPublicKey()
    {
        // A protector that opens to another persona's scalar: the managed checks refuse the pair.
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        store.AddKey(slot, material);
        using var other = SyntheticKeys.Create();
        protector.SubstituteNextUnprotect = SyntheticKeys.Scalar(other);

        Assert.Null(store.OpenKey(slot));
        Assert.Contains("recorded public key", Assert.Single(reports), StringComparison.Ordinal);
        Assert.All(protector.HandedOut, array => Assert.All(array, b => Assert.Equal(0, b)));
    }

    [Fact]
    public void Open_ReturnsNullWhenTheStorageThrows()
    {
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        store.AddKey(slot, material);
        storage.ReadFailure = new UnauthorizedAccessException("denied");

        Assert.Null(store.OpenKey(slot));
        Assert.Null(store.OpenSigner(slot));
        Assert.Equal(2, reports.Count);
    }

    [Fact]
    public void Reports_NameTheSlotAndNeverAnIdentityOrKeyBytes()
    {
        var store = NewStore();
        var slot = NewSlot();
        using var key = SyntheticKeys.Create();
        using var material = PersonaKeyMaterial.FromEcdsa(key);
        store.AddKey(slot, material);
        protector.Locked = true;
        Assert.Null(store.OpenKey(slot));

        var report = Assert.Single(reports);
        Assert.Contains(slot.ToString(), report, StringComparison.Ordinal);
        Assert.DoesNotContain("psn_", report, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticKeys.PrivateHex(key), report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SyntheticKeys.PublicHex(material.PublicKey).Substring(2, 16), report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PeekPublicKey_ReadsTheHeaderOnly_AndNeverOpensTheKey()
    {
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        store.AddKey(slot, material);
        protector.Calls.Clear();

        Assert.Equal(PersonaKeyPeekStatus.Held, store.PeekPublicKey(slot, out var publicKey));
        Assert.Equal(material.PublicKey, publicKey);

        Assert.Equal(PersonaKeyPeekStatus.Missing, store.PeekPublicKey(NewSlot(), out publicKey));
        Assert.Null(publicKey);
        Assert.Equal(PersonaKeyPeekStatus.Missing, store.PeekPublicKey(default, out publicKey));
        Assert.Null(publicKey);

        var other = NewSlot();
        storage.Plant(other, storage.Held(slot));
        Assert.Equal(PersonaKeyPeekStatus.NamesAnotherSlot, store.PeekPublicKey(other, out publicKey));
        Assert.Null(publicKey);

        storage.Plant(other, [1, 2, 3]);
        Assert.Equal(PersonaKeyPeekStatus.Unreadable, store.PeekPublicKey(other, out publicKey));
        Assert.Null(publicKey);

        // Held says the header reads here, not that the key opens: locked on this account, it still peeks.
        protector.Locked = true;
        Assert.Equal(PersonaKeyPeekStatus.Held, store.PeekPublicKey(slot, out _));
        protector.Locked = false;

        // Another protector's envelope never opens here.
        var foreign = NewSlot();
        using (var foreignKey = PersonaKeyMaterial.Generate())
        {
            new ProtectedPersonaKeyStore(storage, new FakeProtector("test.other.v1")).AddKey(foreign, foreignKey);
        }

        Assert.Equal(PersonaKeyPeekStatus.Unreadable, store.PeekPublicKey(foreign, out publicKey));
        Assert.Null(publicKey);

        storage.ReadFailure = new System.IO.IOException("The file is being used by another process.");
        Assert.Equal(PersonaKeyPeekStatus.Unreadable, store.PeekPublicKey(slot, out publicKey));
        Assert.Null(publicKey);

        Assert.Empty(protector.Calls);
        Assert.Empty(reports);
    }

    [Fact]
    public void OpenPublicKey_ProvesTheKey_AndLeavesNoScalarBehind()
    {
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        store.AddKey(slot, material);
        protector.HandedOut.Clear();

        Assert.Equal(material.PublicKey, store.OpenPublicKey(slot));
        Assert.All(protector.HandedOut, scalar => Assert.All(scalar, b => Assert.Equal(0, b)));

        Assert.Null(store.OpenPublicKey(NewSlot()));
        protector.Locked = true;
        Assert.Null(store.OpenPublicKey(slot));
    }

    [Fact]
    public void ListHeld_IsTheStoragesListing()
    {
        var store = NewStore();
        var slot = NewSlot();
        using var material = store.GenerateKey();
        store.AddKey(slot, material);
        storage.SkippedEntries = 1;

        var listing = store.ListHeld();
        Assert.Equal(slot, Assert.Single(listing.Slots));
        Assert.Equal(1, listing.Skipped);

        storage.ListFailure = new System.IO.IOException("Access to the path is denied.");
        Assert.Throws<System.IO.IOException>(() => store.ListHeld());
    }

    [Fact]
    public void AListing_IsACopy_AndRefusesWhatCannotBeOne()
    {
        var slots = new System.Collections.Generic.List<PersonaSlotId> { NewSlot() };
        var listing = new PersonaKeyListing(slots, 0);
        slots.Add(NewSlot());
        Assert.Single(listing.Slots);
        Assert.Throws<ArgumentNullException>(() => new PersonaKeyListing(null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PersonaKeyListing(slots, -1));
    }

    [Fact]
    public void TheStoreRefusesAProtectorWithAnInvalidId()
    {
        Assert.Throws<ArgumentException>(() => new ProtectedPersonaKeyStore(storage, new FakeProtector("Bad Id")));
        Assert.Throws<ArgumentNullException>(() => new ProtectedPersonaKeyStore(null!, protector));
        Assert.Throws<ArgumentNullException>(() => new ProtectedPersonaKeyStore(storage, null!));
    }

    [Fact]
    public void TheManagerWorksOverTheStore_EndToEnd()
    {
        var store = NewStore();
        var manager = new PersonaManager(store, new HandleBackupCodec());
        var main = manager.Create("Main");
        var alt = manager.Create("Alt");
        Assert.Equal(2, storage.Count);
        Assert.NotEqual(main.Id, alt.Id);

        manager.Select(alt.Slot);
        Assert.Equal(PersonaSignerAvailability.Available, manager.TryOpenActiveSigner(out var lease));
        using (lease)
        {
            var document = Documents.SignedRetraction(lease!.Signer);
            var verified = SignedDocumentCodec.Verify(document);
            Assert.Equal(alt.PublicKey, verified.PublicKey);
        }

        // A key locked on this account is reported as unavailable, and the record stays.
        protector.Locked = true;
        Assert.Equal(PersonaSignerAvailability.KeyUnavailable, manager.TryOpenActiveSigner(out var none));
        Assert.Null(none);
        Assert.Equal(2, manager.Personas.Count);
        protector.Locked = false;

        // A backup export opens the key once and the codec gets a copy the manager disposes.
        using var secret = PersonaBackupSecret.FromText("correct horse battery staple");
        var backup = manager.ExportBackup(main.Slot, secret);
        Assert.NotEmpty(backup);
    }
}
