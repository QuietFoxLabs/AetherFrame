using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Signing;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The persona model of decision D3: several independent personas, one of them selected by the
/// player, switched only on purpose, never bound to anything about a character, and never able to
/// touch a Plate. Every key is synthetic and in memory.
/// </summary>
public class PersonaManagerTests
{
    private readonly InMemoryPersonaKeyStore store = new();
    private readonly HandleBackupCodec codec = new();

    private PersonaManager NewManager() => new(store, codec);

    [Fact]
    public void Constructor_RefusesNullSeams()
    {
        Assert.Throws<ArgumentNullException>(() => new PersonaManager(null!, codec));
        Assert.Throws<ArgumentNullException>(() => new PersonaManager(store, null!));
    }

    [Fact]
    public void Create_HoldsSeveralIndependentPersonas()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        var alt = manager.Create("RP alt");
        var third = manager.Create("Third");

        var personas = manager.Personas;
        Assert.Equal(["Main", "RP alt", "Third"], personas.Select(p => p.Label));
        Assert.Equal(3, personas.Select(p => p.Slot).Distinct().Count());
        Assert.Equal(3, personas.Select(p => p.Id).Distinct().Count());
        Assert.Equal(3, personas.Select(p => p.PublicKey).Distinct().Count());
        Assert.Equal(3, store.Count);
        Assert.Equal(3, store.CallsTo("GenerateKey"));
        Assert.Equal(3, store.CallsTo("AddKey"));
        Assert.All(personas, p => Assert.True(store.Holds(p.Slot)));
        Assert.All(store.HandedOut, m => Assert.True(Disposal.IsDisposed(m)));
        Assert.All(personas, p => Assert.False(p.Slot.IsEmpty));
        Assert.All(personas, p => Assert.False(p.Id.IsEmpty));
        Assert.Same(main, personas[0]);
        Assert.Same(alt, personas[1]);
        Assert.Same(third, personas[2]);
    }

    [Fact]
    public void Create_NeverSelectsThePersona()
    {
        var manager = NewManager();
        manager.Create("Main");
        Assert.Null(manager.Active);
        manager.Create("Second");
        Assert.Null(manager.Active);
    }

    [Fact]
    public void Create_ChecksTheLabelBeforeMakingAnyKey()
    {
        var manager = NewManager();
        var exception = Assert.Throws<PersonaException>(() => manager.Create("   "));
        Assert.Equal(PersonaError.InvalidLabel, exception.Error);
        Assert.Empty(store.Calls);
        Assert.Empty(manager.Personas);
    }

    [Fact]
    public void Create_RefusesADuplicateIdentity()
    {
        var manager = NewManager();
        using var shared = SyntheticKeys.Create();
        store.NextKey = () => SyntheticKeys.Copy(shared);
        var first = manager.Create("Main");

        manager.Select(first.Slot);
        var heldBefore = store.Held(first.Slot);

        store.NextKey = () => SyntheticKeys.Copy(shared);
        var exception = Assert.Throws<PersonaException>(() => manager.Create("Impostor"));
        Assert.Equal(PersonaError.DuplicateIdentity, exception.Error);
        Assert.DoesNotContain(first.Id.ToString(), exception.Message, StringComparison.Ordinal);

        // Refused before the store committed anything: no orphaned key, the first persona's key is
        // the same object, and the duplicate the store generated was disposed.
        Assert.Single(manager.Personas);
        Assert.Same(first, manager.Personas[0]);
        Assert.Same(first, manager.Active);
        Assert.Equal(1, store.CallsTo("AddKey"));
        Assert.Equal(1, store.Count);
        Assert.Same(heldBefore, store.Held(first.Slot));
        Assert.True(Disposal.IsDisposed(store.HandedOut[^1]));
    }

    [Fact]
    public void Select_SwitchesTheActivePersonaAndNothingElse()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        var alt = manager.Create("RP alt");
        var before = manager.Personas;
        var callsBefore = store.Calls.Count;

        Assert.Same(main, manager.Select(main.Slot));
        Assert.Same(main, manager.Active);
        Assert.Same(alt, manager.Select(alt.Slot));
        Assert.Same(alt, manager.Active);
        Assert.Same(main, manager.Select(main.Slot));
        Assert.Same(main, manager.Active);

        var after = manager.Personas;
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Same(before[i], after[i]);
        }

        Assert.Equal(callsBefore, store.Calls.Count);
    }

    [Fact]
    public void Select_UnknownSlot_ThrowsAndLeavesTheSelection()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        var stranger = PersonaSlotId.NewId();

        var exception = Assert.Throws<PersonaException>(() => manager.Select(stranger));
        Assert.Equal(PersonaError.UnknownPersona, exception.Error);
        Assert.Null(manager.Active);

        manager.Select(main.Slot);
        Assert.Throws<PersonaException>(() => manager.Select(stranger));
        Assert.Same(main, manager.Active);
        Assert.Throws<PersonaException>(() => manager.Select(default));
        Assert.Same(main, manager.Active);
    }

    [Fact]
    public void Deselect_LeavesNoActivePersona()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        manager.Select(main.Slot);
        manager.Deselect();
        Assert.Null(manager.Active);
        Assert.Single(manager.Personas);
        manager.Deselect();
        Assert.Null(manager.Active);
    }

    [Fact]
    public void TryGet_FindsOnlyHeldSlots()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        Assert.True(manager.TryGet(main.Slot, out var found));
        Assert.Same(main, found);
        Assert.False(manager.TryGet(PersonaSlotId.NewId(), out var missing));
        Assert.Null(missing);
        Assert.False(manager.TryGet(default, out _));
    }

    [Fact]
    public void Personas_IsASnapshot()
    {
        var manager = NewManager();
        manager.Create("Main");
        var snapshot = manager.Personas;
        manager.Create("Second");
        Assert.Single(snapshot);
        Assert.Equal(2, manager.Personas.Count);
    }

    [Fact]
    public void Rename_ChangesOnlyTheLabel()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        var alt = manager.Create("RP alt");
        manager.Select(alt.Slot);

        var renamed = manager.Rename(alt.Slot, "  Roleplay  ");
        Assert.Equal("Roleplay", renamed.Label);
        Assert.Equal(alt.Slot, renamed.Slot);
        Assert.Equal(alt.Id, renamed.Id);
        Assert.Equal(alt.PublicKey, renamed.PublicKey);
        Assert.Equal("RP alt", alt.Label);

        Assert.Same(main, manager.Personas[0]);
        Assert.Same(renamed, manager.Personas[1]);
        Assert.Same(renamed, manager.Active);
        Assert.Equal(2, store.CallsTo("GenerateKey"));
        Assert.Equal(2, store.CallsTo("AddKey"));
        Assert.Equal(4, store.Calls.Count);
    }

    [Fact]
    public void Rename_RefusesABadLabelOrAnUnknownSlot()
    {
        var manager = NewManager();
        var main = manager.Create("Main");

        var bad = Assert.Throws<PersonaException>(() => manager.Rename(main.Slot, "\u0000"));
        Assert.Equal(PersonaError.InvalidLabel, bad.Error);
        Assert.Same(main, manager.Personas[0]);

        var unknown = Assert.Throws<PersonaException>(() => manager.Rename(PersonaSlotId.NewId(), "Fine"));
        Assert.Equal(PersonaError.UnknownPersona, unknown.Error);
        Assert.Same(main, manager.Personas[0]);
    }

    [Fact]
    public void TryOpenActiveSigner_WithoutASelection_ReportsNoActivePersona()
    {
        var manager = NewManager();
        Assert.Equal(PersonaSignerAvailability.NoActivePersona, manager.TryOpenActiveSigner(out var lease));
        Assert.Null(lease);

        manager.Create("Main");
        Assert.Equal(PersonaSignerAvailability.NoActivePersona, manager.TryOpenActiveSigner(out lease));
        Assert.Null(lease);
        Assert.Null(manager.Active);
        Assert.Equal(0, store.CallsTo("OpenSigner"));
        Assert.Single(manager.Personas);
    }

    [Fact]
    public void TryOpenActiveSigner_WhenTheStoreCannotOpenTheKey_ReportsUnavailable()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        manager.Select(main.Slot);
        store.Lock(main.Slot);

        Assert.Equal(PersonaSignerAvailability.KeyUnavailable, manager.TryOpenActiveSigner(out var lease));
        Assert.Null(lease);
        Assert.Same(main, manager.Active);
        Assert.Single(manager.Personas);

        store.Unlock(main.Slot);
        Assert.Equal(PersonaSignerAvailability.Available, manager.TryOpenActiveSigner(out lease));
        lease!.Dispose();
    }

    [Fact]
    public void TryOpenActiveSigner_SignsAsTheActivePersona()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        var alt = manager.Create("RP alt");
        manager.Select(alt.Slot);

        Assert.Equal(PersonaSignerAvailability.Available, manager.TryOpenActiveSigner(out var lease));
        using (lease)
        {
            Assert.NotNull(lease);
            Assert.Same(alt, lease.Persona);
            Assert.Equal(alt.PublicKey, lease.Signer.PublicKey);
            var verified = SignedDocumentCodec.Verify(Documents.SignedRetraction(lease.Signer));
            Assert.Equal(alt.Id, verified.Persona);
            Assert.NotEqual(main.Id, verified.Persona);
        }

        Assert.Equal(1, store.CallsTo("OpenSigner"));
    }

    [Fact]
    public void TryOpenActiveSigner_RefusesAKeyThatIsNotTheActivePersonas()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        var alt = manager.Create("RP alt");
        manager.Select(main.Slot);
        store.Impersonate[main.Slot] = alt.Slot;

        var exception = Assert.Throws<PersonaException>(() => manager.TryOpenActiveSigner(out _));
        Assert.Equal(PersonaError.InvalidKeyMaterial, exception.Error);
        Assert.Same(main, manager.Active);
    }

    [Fact]
    public void ALease_IsRevokedWhenThePlayerSwitches()
    {
        // NETWORK1.md, system 1: nothing signs for a persona that is not the active one. A lease
        // opened for main stops signing the moment the player selects the alt, and stays revoked
        // even if main is selected again; the alt's own lease signs as the alt.
        var manager = NewManager();
        var main = manager.Create("Main");
        var alt = manager.Create("RP alt");
        manager.Select(main.Slot);
        Assert.Equal(PersonaSignerAvailability.Available, manager.TryOpenActiveSigner(out var lease));
        using var mainLease = lease!;
        Assert.Equal(main.Id, SignedDocumentCodec.Verify(Documents.SignedRetraction(mainLease.Signer)).Persona);

        manager.Select(alt.Slot);
        var revoked = Assert.Throws<PersonaException>(() => Documents.SignedRetraction(mainLease.Signer));
        Assert.Equal(PersonaError.LeaseRevoked, revoked.Error);
        Assert.Same(main, mainLease.Persona);

        Assert.Equal(PersonaSignerAvailability.Available, manager.TryOpenActiveSigner(out lease));
        using var altLease = lease!;
        Assert.Same(alt, altLease.Persona);
        Assert.Equal(alt.Id, SignedDocumentCodec.Verify(Documents.SignedRetraction(altLease.Signer)).Persona);

        manager.Select(main.Slot);
        Assert.Equal(PersonaError.LeaseRevoked, Assert.Throws<PersonaException>(() => Documents.SignedRetraction(mainLease.Signer)).Error);
        Assert.Equal(PersonaError.LeaseRevoked, Assert.Throws<PersonaException>(() => Documents.SignedRetraction(altLease.Signer)).Error);
    }

    [Fact]
    public void ALease_DisposeReleasesTheSigner()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        manager.Select(main.Slot);
        manager.TryOpenActiveSigner(out var lease);
        var signer = lease!.Signer;

        lease.Dispose();
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.Signer);
        Assert.Throws<ObjectDisposedException>(() => Documents.SignedRetraction(signer));
        Assert.Same(main, lease.Persona);
    }

    [Fact]
    public void Personas_AreIsolatedFromEachOther()
    {
        var manager = NewManager();
        var main = manager.Create("Main");
        var alt = manager.Create("RP alt");

        manager.Select(main.Slot);
        manager.TryOpenActiveSigner(out var opened);
        using var mainLease = opened!;

        // Main's signer refuses an input framed for the alt (whatever the payload), and a document it
        // signs is never the alt's.
        var altInput = SigningInput.Create(DocumentType.ProfileRetraction, alt.PublicKey, [0x01, 0x02, 0x03]);
        var refusal = Assert.Throws<ProtocolException>(() => mainLease.Signer.Sign(altInput));
        Assert.Equal(ProtocolError.InvalidKey, refusal.Error);
        var verified = SignedDocumentCodec.Verify(Documents.SignedRetraction(mainLease.Signer));
        Assert.Equal(main.Id, verified.Persona);
        Assert.NotEqual(alt.Id, verified.Persona);

        // Renaming one leaves the other's record untouched, as the same object.
        manager.Rename(alt.Slot, "Renamed alt");
        Assert.Same(main, manager.Personas[0]);
        Assert.Equal("Main", main.Label);

        // The store holds one key per persona and nothing links them.
        Assert.NotEqual(store.Held(main.Slot).PublicKey, store.Held(alt.Slot).PublicKey);
    }

    [Fact]
    public void ConcurrentUse_KeepsTheStateConsistent()
    {
        var manager = NewManager();
        const int threads = 8;
        const int perThread = 25;
        var created = new List<PersonaRecord>[threads];
        Parallel.For(0, threads, t =>
        {
            created[t] = new List<PersonaRecord>();
            for (var i = 0; i < perThread; i++)
            {
                var record = manager.Create($"T{t}-{i}");
                created[t].Add(record);
                manager.Select(record.Slot);
                manager.TryGet(record.Slot, out _);
                _ = manager.Personas;
                _ = manager.Active;
                if (manager.TryOpenActiveSigner(out var lease) == PersonaSignerAvailability.Available)
                {
                    lease!.Dispose();
                }
            }
        });

        var personas = manager.Personas;
        Assert.Equal(threads * perThread, personas.Count);
        Assert.Equal(personas.Count, personas.Select(p => p.Slot).Distinct().Count());
        Assert.Equal(personas.Count, personas.Select(p => p.Id).Distinct().Count());
        Assert.Equal(personas.Count, store.Count);
        Assert.NotNull(manager.Active);
        Assert.Contains(manager.Active, personas);
        Assert.All(created.SelectMany(list => list), record => Assert.Contains(record, personas));
    }

    [Fact]
    public void TheModel_HasNoInputForACharacterAnAccountOrAPlate()
    {
        // Decision D3: no automatic binding. The surface has no parameter through which a Content ID,
        // a character, a World, an account or a Plate could be handed in, so no code path can bind one.
        var forbidden = new[] { "Character", "Content", "Plate", "Profile", "World", "Account", "Binding", "Guid" };
        foreach (var type in typeof(PersonaManager).Assembly.GetExportedTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var parameter in method.GetParameters())
                {
                    var parameterType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
                    Assert.NotEqual(typeof(ulong), parameterType);
                    Assert.NotEqual(typeof(long), parameterType);
                    Assert.NotEqual(typeof(Guid), parameterType);
                    foreach (var word in forbidden)
                    {
                        Assert.DoesNotContain(word, parameter.Name!, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain(word, parameterType.Name, StringComparison.Ordinal);
                    }
                }
            }
        }
    }
}
