using System;
using System.Linq;
using AetherFrame.Protocol.Documents;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// Custody of private keys across refusals and store faults. A key is committed to the store only
/// after the manager has checked it, and nothing refuses after the commit, so a refused create or
/// restore never leaves a key in the store without a record; the manager never deletes a key, so a
/// failed operation can never remove a valid persona's; and every material the manager is handed is
/// disposed whatever happens. Every key is synthetic and in memory.
/// </summary>
public class PersonaCustodyTests
{
    private readonly InMemoryPersonaKeyStore store = new();
    private readonly HandleBackupCodec codec = new();

    private static PersonaBackupSecret Secret() => PersonaBackupSecret.FromText("correct horse battery staple");

    private PersonaManager NewManager() => new(store, codec);

    [Fact]
    public void Create_CommitsOnlyAfterTheIdentityIsChecked()
    {
        var manager = NewManager();
        var record = manager.Create("Main");
        Assert.Equal(["GenerateKey", "AddKey"], store.Calls);
        Assert.Equal(record.PublicKey, store.Held(record.Slot).PublicKey);

        // The store keeps its own copy, never the object it was given, and the manager disposed that.
        var given = Assert.Single(store.Received);
        Assert.NotSame(given, store.Held(record.Slot));
        Assert.True(Disposal.IsDisposed(given));
        Assert.False(Disposal.IsDisposed(store.Held(record.Slot)));
    }

    [Fact]
    public void Create_OfADuplicateIdentity_CommitsNothing()
    {
        var manager = NewManager();
        using var shared = SyntheticKeys.Create();
        store.NextKey = () => SyntheticKeys.Copy(shared);
        var first = manager.Create("Main");
        var callsBefore = store.Calls.Count;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            store.NextKey = () => SyntheticKeys.Copy(shared);
            Assert.Equal(PersonaError.DuplicateIdentity, Assert.Throws<PersonaException>(() => manager.Create("Again")).Error);
        }

        // Three refusals, three keys generated, none committed: no orphan accumulates.
        Assert.Equal(1, store.Count);
        Assert.Equal(1, store.CallsTo("AddKey"));
        Assert.Equal(callsBefore + 3, store.Calls.Count);
        Assert.Equal(first.Slot, Assert.Single(store.Slots));
        Assert.All(store.HandedOut, m => Assert.True(Disposal.IsDisposed(m)));
        Assert.Single(manager.Personas);
    }

    [Fact]
    public void Create_WhenTheStoreCannotGenerate_ChangesNothing()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        manager.Select(main.Slot);

        store.FailNextGenerate = new InvalidOperationException("The platform cannot make a key.");
        Assert.Throws<InvalidOperationException>(() => manager.Create("Second"));

        store.GenerateNothing = true;
        Assert.Equal(PersonaError.InvalidKeyMaterial, Assert.Throws<PersonaException>(() => manager.Create("Third")).Error);
        store.GenerateNothing = false;

        Assert.Single(manager.Personas);
        Assert.Same(main, manager.Active);
        Assert.Equal(1, store.Count);
        Assert.Equal(1, store.CallsTo("AddKey"));
    }

    [Fact]
    public void Create_WhenTheStoreFailsToCommit_RecordsNothingAndDisposesTheKey()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        manager.Select(main.Slot);
        var heldBefore = store.Held(main.Slot);

        store.FailNextAdd = new InvalidOperationException("The disk is full.");
        var failure = Assert.Throws<InvalidOperationException>(() => manager.Create("Second"));
        Assert.Equal("The disk is full.", failure.Message);

        Assert.Single(manager.Personas);
        Assert.Same(main, manager.Active);
        Assert.Equal(1, store.Count);
        Assert.Same(heldBefore, store.Held(main.Slot));
        Assert.True(Disposal.IsDisposed(store.HandedOut[^1]));
        Assert.True(Disposal.IsDisposed(store.Received[^1]));

        // The installation carries on: the next create works and the first persona still signs.
        var second = manager.Create("Second");
        Assert.Equal(2, store.Count);
        Assert.True(store.Holds(second.Slot));
        AssertActiveSignsAs(manager, main);
    }

    [Fact]
    public void Create_WhenAStoreBreaksAtomicity_ReportsItAndDeletesNothing()
    {
        // A store that commits and then throws breaks its contract. The manager does not hide that
        // and does not try to repair it by deleting: the exception is the caller's, no record is
        // added, and the existing persona and its key are untouched. (The orphan this store made is
        // the store's own fault, visible in its exception; the manager has no way to delete a key.)
        var manager = NewManager();
        var main = manager.Create("Main");
        var heldBefore = store.Held(main.Slot);

        store.FailNextAddAfterCommit = new InvalidOperationException("Committed, then failed.");
        Assert.Throws<InvalidOperationException>(() => manager.Create("Second"));

        Assert.Single(manager.Personas);
        Assert.Same(heldBefore, store.Held(main.Slot));
        Assert.False(Disposal.IsDisposed(heldBefore));
        Assert.DoesNotContain(typeof(IPersonaKeyStore).GetMethods(), m => m.Name.Contains("Delete", StringComparison.Ordinal) || m.Name.Contains("Remove", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_NeverReplacesAKeyTheStoreAlreadyHolds()
    {
        // A store refusing the slot it was given (as it must when it already holds one there) leaves
        // the manager with nothing recorded and every existing key where it was.
        var manager = NewManager();
        var main = manager.Create("Main");
        store.FailNextAdd = new InvalidOperationException("This store already holds a key under that slot and never replaces one.");
        Assert.Throws<InvalidOperationException>(() => manager.Create("Second"));
        Assert.Single(manager.Personas);
        Assert.Equal(1, store.Count);
        Assert.True(store.Holds(main.Slot));

        // And the double's own rule: AddKey under a held slot throws and leaves the key.
        using var other = SyntheticKeys.Material();
        var held = store.Held(main.Slot);
        Assert.Throws<InvalidOperationException>(() => store.AddKey(main.Slot, other));
        Assert.Same(held, store.Held(main.Slot));
    }

    [Fact]
    public void Restore_OfADuplicate_NeverReachesTheStore()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        using var secret = Secret();
        var backup = manager.ExportBackup(main.Slot, secret);
        var addsBefore = store.CallsTo("AddKey");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(PersonaRestoreStatus.AlreadyPresent, manager.RestoreBackup(backup, secret, "Again").Status);
        }

        Assert.Equal(addsBefore, store.CallsTo("AddKey"));
        Assert.Equal(1, store.Count);
        Assert.All(codec.HandedOut, m => Assert.True(Disposal.IsDisposed(m)));
    }

    [Fact]
    public void Restore_WhenTheStoreFailsToCommit_RecordsNothingAndDisposesTheKey()
    {
        var home = new PersonaManager(new InMemoryPersonaKeyStore(), codec);
        var original = home.Create("Main");
        using var secret = Secret();
        var backup = home.ExportBackup(original.Slot, secret);

        var manager = NewManager();
        var existing = manager.Create("Existing");
        manager.Select(existing.Slot);
        var heldBefore = store.Held(existing.Slot);

        store.FailNextAdd = new InvalidOperationException("The disk is full.");
        Assert.Throws<InvalidOperationException>(() => manager.RestoreBackup(backup, secret, "Restored"));

        Assert.Single(manager.Personas);
        Assert.Same(existing, manager.Active);
        Assert.Equal(1, store.Count);
        Assert.Same(heldBefore, store.Held(existing.Slot));
        Assert.True(Disposal.IsDisposed(codec.HandedOut[^1]));

        // Retrying once the store works restores the persona.
        var retried = manager.RestoreBackup(backup, secret, "Restored");
        Assert.Equal(PersonaRestoreStatus.Restored, retried.Status);
        Assert.Equal(original.Id, retried.Persona!.Id);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Restore_WhenTheCodecOpensNothing_ReportsInvalidKeyAndCommitsNothing()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        using var secret = Secret();
        var backup = manager.ExportBackup(main.Slot, secret);

        var away = new PersonaManager(new InMemoryPersonaKeyStore(), codec);
        codec.OpenNothing = true;
        var result = away.RestoreBackup(backup, secret, "Main");
        Assert.Equal(PersonaRestoreStatus.InvalidKey, result.Status);
        Assert.Null(result.Persona);
        Assert.Empty(away.Personas);
    }

    [Fact]
    public void AStoreThatHoldsAnotherKey_IsCaughtAtUse_AndNeverSignsForTheWrongPersona()
    {
        // A faulty store commits a different key than it was given. The manager cannot see inside the
        // store at commit time, but every later use compares public keys: the lease is refused, the
        // export is refused, and nothing is ever signed as another persona.
        var manager = NewManager();
        using var substitute = SyntheticKeys.Create();
        store.SubstituteNextAdd = () => SyntheticKeys.Copy(substitute);
        var main = manager.Create("Main");
        Assert.NotEqual(main.PublicKey, store.Held(main.Slot).PublicKey);
        manager.Select(main.Slot);

        Assert.Equal(PersonaError.InvalidKeyMaterial, Assert.Throws<PersonaException>(() => manager.TryOpenActiveSigner(out _)).Error);
        using var secret = Secret();
        Assert.Equal(PersonaError.InvalidKeyMaterial, Assert.Throws<PersonaException>(() => manager.ExportBackup(main.Slot, secret)).Error);
        Assert.Equal(0, codec.WriteCalls);
        Assert.Same(main, manager.Active);
    }

    [Fact]
    public void Export_DisposesTheOpenedKeyWhenTheCodecThrows()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        using var secret = Secret();
        codec.FailNextWrite = new InvalidOperationException("The codec failed.");
        Assert.Throws<InvalidOperationException>(() => manager.ExportBackup(main.Slot, secret));

        var opened = store.HandedOut[^1];
        Assert.True(Disposal.IsDisposed(opened));
        Assert.NotSame(opened, store.Held(main.Slot));
        Assert.False(Disposal.IsDisposed(store.Held(main.Slot)));

        // The secret is the caller's: the manager never disposes it, so the caller can retry.
        Assert.Equal("[backup secret]", secret.ToString());
        Assert.Equal(HandleBackupCodec.Length, manager.ExportBackup(main.Slot, secret).Length);
    }

    [Fact]
    public void Export_DisposesAKeyOfTheWrongPersona()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        var alt = manager.Create("RP alt");
        store.Impersonate[main.Slot] = alt.Slot;
        using var secret = Secret();
        Assert.Throws<PersonaException>(() => manager.ExportBackup(main.Slot, secret));
        Assert.True(Disposal.IsDisposed(store.HandedOut[^1]));
    }

    [Fact]
    public void EveryMaterialTheManagerIsHanded_IsDisposed()
    {
        var manager = NewManager();
        using var secret = Secret();
        var main = manager.Create("Main");
        var backup = manager.ExportBackup(main.Slot, secret);
        manager.RestoreBackup(backup, secret, "Again");
        var away = new PersonaManager(new InMemoryPersonaKeyStore(), codec);
        away.RestoreBackup(backup, secret, "Away");

        Assert.NotEmpty(store.HandedOut);
        Assert.NotEmpty(codec.HandedOut);
        Assert.All(store.HandedOut.Concat(codec.HandedOut).Concat(store.Received), m => Assert.True(Disposal.IsDisposed(m)));
    }

    private static void AssertActiveSignsAs(PersonaManager manager, PersonaRecord expected)
    {
        Assert.Equal(PersonaSignerAvailability.Available, manager.TryOpenActiveSigner(out var lease));
        using (lease)
        {
            Assert.Equal(expected.Id, SignedDocumentCodec.Verify(Documents.SignedRetraction(lease!.Signer)).Persona);
        }
    }
}
