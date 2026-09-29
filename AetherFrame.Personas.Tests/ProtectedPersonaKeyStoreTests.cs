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
    public void AddKey_WhenTheStorageHoldsSomethingElse_ReportsIt()
    {
        // A storage that breaks its contract (holds different bytes) is not hidden: the read-back
        // fails and the caller learns the key is not in custody, even though the storage holds bytes.
        var store = NewStore();
        using var material = store.GenerateKey();
        storage.SubstituteNextWrite = bytes => { bytes[^1] ^= 0x01; return bytes; };

        var failure = Assert.Throws<PersonaException>(() => store.AddKey(NewSlot(), material));
        Assert.Equal(PersonaError.CustodyFailed, failure.Error);
        Assert.Null(failure.InnerException);
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

        // The whole envelope of the first under the second's slot: names another slot.
        storage.Plant(second, storage.Held(first));
        Assert.Null(store.OpenKey(second));
        Assert.Contains("another slot", reports.Last(), StringComparison.Ordinal);

        // The first's blob inside the second's header: the context differs, so it does not open.
        Assert.True(ProtectedKeyEnvelope.TryDecode(storage.Held(first), out var firstDecoded));
        Assert.True(ProtectedKeyEnvelope.TryDecode(storage.Held(second), out var secondDecoded));
        storage.Plant(second, ProtectedKeyEnvelope.Encode(secondDecoded.Context, firstDecoded.Blob));
        Assert.Null(store.OpenKey(second));

        // The second's own blob under the first's public key: the protector's context differs too.
        var header = ProtectedKeyEnvelope.EncodeHeader(second, FakeProtector.DefaultId, firstKey.PublicKey);
        storage.Plant(second, ProtectedKeyEnvelope.Encode(header, secondDecoded.Blob));
        Assert.Null(store.OpenKey(second));
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
