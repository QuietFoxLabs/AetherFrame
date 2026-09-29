using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Signing;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// Decision D3: the player alone chooses the active persona. <see cref="PersonaManager.Select"/> and
/// <see cref="PersonaManager.Deselect"/> are the only operations that change it; every other public
/// operation, succeeding or failing, leaves it exactly as it was; and a signer lease never signs for
/// a persona that is not the active one. The operation table is checked against the manager's
/// public members by reflection, so a new member fails here until it is classified.
/// </summary>
public class PersonaSelectionTests
{
    private const string Phrase = "correct horse battery staple";

    /// <summary>Every public member of the manager that is not <see cref="PersonaManager.Select"/> or <see cref="PersonaManager.Deselect"/>, each with the ways it can succeed and fail.</summary>
    private static readonly (string Member, string Case, Action<World> Run)[] OtherOperations =
    [
        ("get_Personas", "list", w => _ = w.Manager.Personas),
        ("get_Active", "read", w => _ = w.Manager.Active),
        ("Create", "succeeds", w => w.Manager.Create("New")),
        ("Create", "invalid label", w => Refused(() => w.Manager.Create(" "))),
        ("Create", "duplicate identity", w =>
        {
            w.Store.NextKey = () => SyntheticKeys.Copy(w.MainKey);
            Refused(() => w.Manager.Create("Duplicate"));
        }),
        ("Create", "store cannot commit", w =>
        {
            w.Store.FailNextAdd = new InvalidOperationException("The store fails.");
            Refused(() => w.Manager.Create("Failing"));
        }),
        ("Rename", "the active persona", w => w.Manager.Rename(w.Main.Slot, "Main renamed")),
        ("Rename", "another persona", w => w.Manager.Rename(w.Alt.Slot, "Alt renamed")),
        ("Rename", "invalid label", w => Refused(() => w.Manager.Rename(w.Main.Slot, ""))),
        ("Rename", "unknown slot", w => Refused(() => w.Manager.Rename(PersonaSlotId.NewId(), "Nobody"))),
        ("TryGet", "held", w => w.Manager.TryGet(w.Alt.Slot, out _)),
        ("TryGet", "unknown", w => w.Manager.TryGet(PersonaSlotId.NewId(), out _)),
        ("TryOpenActiveSigner", "open and sign", w =>
        {
            if (w.Manager.TryOpenActiveSigner(out var lease) == PersonaSignerAvailability.Available)
            {
                using (lease)
                {
                    Documents.SignedRetraction(lease!.Signer);
                }
            }
        }),
        ("TryOpenActiveSigner", "key locked", w =>
        {
            w.Store.Lock(w.Main.Slot);
            w.Manager.TryOpenActiveSigner(out _);
            w.Store.Unlock(w.Main.Slot);
        }),
        ("ExportBackup", "succeeds", w => w.Manager.ExportBackup(w.Alt.Slot, w.Secret)),
        ("ExportBackup", "unknown slot", w => Refused(() => w.Manager.ExportBackup(PersonaSlotId.NewId(), w.Secret))),
        ("ExportBackup", "key locked", w =>
        {
            w.Store.Lock(w.Alt.Slot);
            Refused(() => w.Manager.ExportBackup(w.Alt.Slot, w.Secret));
            w.Store.Unlock(w.Alt.Slot);
        }),
        ("InspectBackup", "supported", w => w.Manager.InspectBackup(w.FarBackup)),
        ("InspectBackup", "malformed", w => w.Manager.InspectBackup(new byte[3])),
        ("InspectBackup", "incoherent inspection", w =>
        {
            w.Codec.InspectOverride = _ => null;
            Refused(() => w.Manager.InspectBackup(w.FarBackup));
            w.Codec.InspectOverride = null;
        }),
        ("RestoreBackup", "a new persona", w => Assert.Equal(PersonaRestoreStatus.Restored, w.Manager.RestoreBackup(w.FarBackup, w.Secret, "Far").Status)),
        ("RestoreBackup", "the active persona again", w => Assert.Equal(PersonaRestoreStatus.AlreadyPresent, w.Manager.RestoreBackup(w.MainBackup, w.Secret, "Main again").Status)),
        ("RestoreBackup", "another held persona again", w => Assert.Equal(PersonaRestoreStatus.AlreadyPresent, w.Manager.RestoreBackup(w.AltBackup, w.Secret, "Alt again").Status)),
        ("RestoreBackup", "unsupported version", w => Assert.Equal(PersonaRestoreStatus.UnsupportedVersion, w.Manager.RestoreBackup(HandleBackupCodec.WithVersion(w.FarBackup, 7), w.Secret, "Far").Status)),
        ("RestoreBackup", "malformed", w => Assert.Equal(PersonaRestoreStatus.Malformed, w.Manager.RestoreBackup(new byte[5], w.Secret, "Far").Status)),
        ("RestoreBackup", "wrong secret", w =>
        {
            using var wrong = PersonaBackupSecret.FromText("wrong");
            Assert.Equal(PersonaRestoreStatus.CannotOpen, w.Manager.RestoreBackup(w.FarBackup, wrong, "Far").Status);
        }),
        ("RestoreBackup", "store cannot commit", w =>
        {
            w.Store.FailNextAdd = new InvalidOperationException("The store fails.");
            Refused(() => w.Manager.RestoreBackup(w.FarBackup, w.Secret, "Far"));
        }),
        ("RestoreBackup", "invalid label", w => Refused(() => w.Manager.RestoreBackup(w.FarBackup, w.Secret, "\n"))),
        ("RestoreBackup", "incoherent inspection", w =>
        {
            w.Codec.InspectOverride = _ => HandleBackupCodec.Forged((PersonaBackupStatus)42, 1);
            Refused(() => w.Manager.RestoreBackup(w.FarBackup, w.Secret, "Far"));
            w.Codec.InspectOverride = null;
        }),
        ("Select", "unknown slot fails", w => Refused(() => w.Manager.Select(PersonaSlotId.NewId()))),
        ("Select", "empty slot fails", w => Refused(() => w.Manager.Select(default))),
    ];

    public static TheoryData<int, bool> EveryOtherOperation()
    {
        var data = new TheoryData<int, bool>();
        for (var index = 0; index < OtherOperations.Length; index++)
        {
            data.Add(index, true);
            data.Add(index, false);
        }

        return data;
    }

    [Fact]
    public void TheOperationTable_CoversEveryPublicMemberOfTheManager()
    {
        var members = typeof(PersonaManager)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet();
        var classified = OtherOperations.Select(o => o.Member).Append("Select").Append("Deselect").ToHashSet();
        Assert.Equal(members.Order(StringComparer.Ordinal), classified.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EveryOtherOperation))]
    public void AnOperationOtherThanSelectOrDeselect_NeverChangesTheSelectionOrRevokesALease(int index, bool withSelection)
    {
        var (member, name, run) = OtherOperations[index];
        using var world = new World();
        PersonaSignerLease? lease = null;
        if (withSelection)
        {
            world.Manager.Select(world.Main.Slot);
            Assert.Equal(PersonaSignerAvailability.Available, world.Manager.TryOpenActiveSigner(out lease));
        }

        using (lease)
        {
            var before = world.Manager.Active?.Slot;
            run(world);
            Assert.True(before == world.Manager.Active?.Slot, $"{member} ({name}) changed the selection");

            // Nothing an operation added is selected: the only selection is the player's.
            Assert.All(world.Manager.Personas.Where(p => p.Slot != world.Main.Slot && p.Slot != world.Alt.Slot), p => Assert.NotEqual(p.Slot, world.Manager.Active?.Slot));

            // Revocation follows the selection only, so a lease opened before still signs as main.
            if (lease is not null)
            {
                var verified = SignedDocumentCodec.Verify(Documents.SignedRetraction(lease.Signer));
                Assert.True(world.Main.Id == verified.Persona, $"{member} ({name}) disturbed the lease");
            }
        }
    }

    [Fact]
    public void SelectAndDeselect_AreHowTheSelectionChanges()
    {
        using var world = new World();
        var manager = world.Manager;
        Assert.Null(manager.Active);
        Assert.Same(world.Main, manager.Select(world.Main.Slot));
        Assert.Equal(world.Main.Slot, manager.Active!.Slot);
        manager.Select(world.Alt.Slot);
        Assert.Equal(world.Alt.Slot, manager.Active!.Slot);
        manager.Select(world.Alt.Slot);
        Assert.Equal(world.Alt.Slot, manager.Active!.Slot);
        manager.Deselect();
        Assert.Null(manager.Active);
    }

    [Fact]
    public void ALease_SurvivesSelectingThePersonaThatIsAlreadyActive()
    {
        using var world = new World();
        world.Manager.Select(world.Main.Slot);
        world.Manager.TryOpenActiveSigner(out var opened);
        using var lease = opened!;
        world.Manager.Select(world.Main.Slot);
        Assert.Equal(world.Main.Id, SignedDocumentCodec.Verify(Documents.SignedRetraction(lease.Signer)).Persona);
    }

    [Fact]
    public void ALease_IsRevokedByDeselect_AndStaysRevoked()
    {
        using var world = new World();
        world.Manager.Select(world.Main.Slot);
        world.Manager.TryOpenActiveSigner(out var opened);
        using var lease = opened!;

        world.Manager.Deselect();
        Assert.Equal(PersonaError.LeaseRevoked, Assert.Throws<PersonaException>(() => Documents.SignedRetraction(lease.Signer)).Error);
        world.Manager.Select(world.Main.Slot);
        Assert.Equal(PersonaError.LeaseRevoked, Assert.Throws<PersonaException>(() => Documents.SignedRetraction(lease.Signer)).Error);

        // A deselect with nothing selected changes nothing, so it revokes nothing.
        world.Manager.TryOpenActiveSigner(out var fresh);
        using var current = fresh!;
        world.Manager.Select(world.Main.Slot);
        Assert.Equal(world.Main.Id, SignedDocumentCodec.Verify(Documents.SignedRetraction(current.Signer)).Persona);
    }

    [Fact]
    public void ALease_NeverHandsOutTheStoresSigner()
    {
        using var world = new World();
        world.Manager.Select(world.Main.Slot);
        world.Manager.TryOpenActiveSigner(out var opened);
        using var lease = opened!;
        Assert.IsNotType<EcdsaPersonaSigner>(lease.Signer);
        Assert.False(lease.Signer is IDisposable);
        Assert.Equal(world.Main.PublicKey, lease.Signer.PublicKey);
    }

    [Fact]
    public void ALease_RefusesAStoreSignerThatStopsReportingThePersona()
    {
        using var world = new World();
        ShiftingSigner? shifting = null;
        world.Store.SignerOverride = slot =>
        {
            shifting = new ShiftingSigner(world.Store.Held(slot).CreateSigner());
            return shifting;
        };
        world.Manager.Select(world.Main.Slot);
        Assert.Equal(PersonaSignerAvailability.Available, world.Manager.TryOpenActiveSigner(out var opened));
        using var lease = opened!;

        shifting!.PublicKey = world.Alt.PublicKey;
        Assert.Equal(PersonaError.InvalidKeyMaterial, Assert.Throws<PersonaException>(() => Documents.SignedRetraction(lease.Signer)).Error);
        Assert.Equal(0, shifting.SignCalls);

        lease.Dispose();
        Assert.True(shifting.Disposed);
    }

    [Fact]
    public void ALease_RefusesAnInputForAnotherPersona_BeforeTheStoresSignerSeesIt()
    {
        using var world = new World();
        ShiftingSigner? shifting = null;
        world.Store.SignerOverride = slot =>
        {
            shifting = new ShiftingSigner(world.Store.Held(slot).CreateSigner());
            return shifting;
        };
        world.Manager.Select(world.Main.Slot);
        world.Manager.TryOpenActiveSigner(out var opened);
        using var lease = opened!;

        var altInput = SigningInput.Create(DocumentType.ProfileRetraction, world.Alt.PublicKey, [0x01]);
        Assert.Equal(ProtocolError.InvalidKey, Assert.Throws<ProtocolException>(() => lease.Signer.Sign(altInput)).Error);
        Assert.Equal(0, shifting!.SignCalls);
    }

    [Fact]
    public void ADisposedLease_SignsNothing()
    {
        using var world = new World();
        world.Manager.Select(world.Main.Slot);
        world.Manager.TryOpenActiveSigner(out var opened);
        var guard = opened!.Signer;
        opened.Dispose();
        opened.Dispose();
        Assert.Throws<ObjectDisposedException>(() => opened.Signer);
        Assert.Throws<ObjectDisposedException>(() => Documents.SignedRetraction(guard));
    }

    [Fact]
    public async Task ConcurrentSwitchingAndRestoration_NeverSelectsARestoredPersona()
    {
        // One thread is the player, switching among four personas and sometimes deselecting. Others
        // restore new and duplicate backups and create personas at the same time, and an observer
        // reads the selection throughout. The selection is only ever one the player chose, and at
        // the end it is exactly the player's last choice.
        using var world = new World();
        var manager = world.Manager;
        var choices = new[] { world.Main, world.Alt, manager.Create("Third"), manager.Create("Fourth") };
        var chosen = choices.Select(c => c.Slot).ToHashSet();

        var home = new PersonaManager(new InMemoryPersonaKeyStore(), world.Codec);
        var fresh = Enumerable.Range(0, 12).Select(i => home.Create($"Far {i}")).Select(p => home.ExportBackup(p.Slot, world.Secret)).ToArray();
        var duplicates = new[] { world.MainBackup, world.AltBackup };

        const int switches = 400;
        PersonaSlotId? last = null;
        var violations = new ConcurrentQueue<string>();
        using var start = new Barrier(5);
        var done = 0;

        var player = Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < switches; i++)
            {
                if (i % 7 == 6)
                {
                    manager.Deselect();
                    last = null;
                }
                else
                {
                    var slot = choices[i % choices.Length].Slot;
                    manager.Select(slot);
                    last = slot;
                }
            }
        });
        var restorers = Enumerable.Range(0, 2).Select(r => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < fresh.Length; i++)
            {
                var status = manager.RestoreBackup(fresh[(i + (r * 5)) % fresh.Length], world.Secret, $"Restored {r}-{i}").Status;
                if (status is not (PersonaRestoreStatus.Restored or PersonaRestoreStatus.AlreadyPresent))
                {
                    violations.Enqueue($"restore returned {status}");
                }

                if (manager.RestoreBackup(duplicates[i % 2], world.Secret, "Duplicate").Status != PersonaRestoreStatus.AlreadyPresent)
                {
                    violations.Enqueue("a duplicate was not reported present");
                }
            }
        })).ToArray();
        var creator = Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 20; i++)
            {
                manager.Create($"Created {i}");
            }
        });
        var observer = Task.Run(() =>
        {
            start.SignalAndWait();
            while (Volatile.Read(ref done) == 0)
            {
                if (manager.Active is { } active && !chosen.Contains(active.Slot))
                {
                    violations.Enqueue("a persona the player never chose was active");
                }
            }
        });

        await Task.WhenAll([player, creator, .. restorers]);
        Volatile.Write(ref done, 1);
        await observer;

        Assert.Empty(violations);
        Assert.Equal(last, manager.Active?.Slot);
        var personas = manager.Personas;
        Assert.Equal(personas.Count, personas.Select(p => p.Id).Distinct().Count());
        Assert.Equal(4 + fresh.Length + 20, personas.Count);
        Assert.Equal(personas.Count, world.Store.Count);
    }

    [Fact]
    public async Task ConcurrentSwitching_RevokesALeaseForGood_AndItOnlyEverSignsAsItsPersona()
    {
        using var world = new World();
        var manager = world.Manager;
        manager.Select(world.Main.Slot);
        manager.TryOpenActiveSigner(out var opened);
        using var lease = opened!;

        var outcomes = new ConcurrentQueue<(int Order, bool Signed)>();
        var order = 0;
        using var start = new Barrier(2);
        var signer = Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 300; i++)
            {
                try
                {
                    var verified = SignedDocumentCodec.Verify(Documents.SignedRetraction(lease.Signer));
                    Assert.Equal(world.Main.Id, verified.Persona);
                    outcomes.Enqueue((Interlocked.Increment(ref order), true));
                }
                catch (PersonaException e) when (e.Error == PersonaError.LeaseRevoked)
                {
                    outcomes.Enqueue((Interlocked.Increment(ref order), false));
                }
            }
        });
        var player = Task.Run(() =>
        {
            start.SignalAndWait();
            Thread.Sleep(5);
            for (var i = 0; i < 50; i++)
            {
                manager.Select(i % 2 == 0 ? world.Alt.Slot : world.Main.Slot);
            }
        });
        await Task.WhenAll(signer, player);

        // Once a signature is refused, none follows: a revoked lease never comes back.
        var sequence = outcomes.OrderBy(o => o.Order).Select(o => o.Signed).ToArray();
        var firstRefusal = Array.IndexOf(sequence, false);
        if (firstRefusal >= 0)
        {
            Assert.All(sequence.Skip(firstRefusal), signed => Assert.False(signed));
        }

        Assert.Equal(PersonaError.LeaseRevoked, Assert.Throws<PersonaException>(() => Documents.SignedRetraction(lease.Signer)).Error);
    }

    private static void Refused(Action action)
    {
        var exception = Record.Exception(action);
        Assert.NotNull(exception);
    }

    /// <summary>An installation with two personas and backups of them and of a persona held elsewhere.</summary>
    private sealed class World : IDisposable
    {
        public World()
        {
            Store = new InMemoryPersonaKeyStore();
            Codec = new HandleBackupCodec();
            Manager = new PersonaManager(Store, Codec);
            Secret = PersonaBackupSecret.FromText(Phrase);
            MainKey = SyntheticKeys.Create();
            Store.NextKey = () => SyntheticKeys.Copy(MainKey);
            Main = Manager.Create("Main");
            Alt = Manager.Create("RP alt");
            MainBackup = Manager.ExportBackup(Main.Slot, Secret);
            AltBackup = Manager.ExportBackup(Alt.Slot, Secret);
            var elsewhere = new PersonaManager(new InMemoryPersonaKeyStore(), Codec);
            var far = elsewhere.Create("Far");
            FarBackup = elsewhere.ExportBackup(far.Slot, Secret);
        }

        public InMemoryPersonaKeyStore Store { get; }

        public HandleBackupCodec Codec { get; }

        public PersonaManager Manager { get; }

        public PersonaBackupSecret Secret { get; }

        public System.Security.Cryptography.ECDsa MainKey { get; }

        public PersonaRecord Main { get; }

        public PersonaRecord Alt { get; }

        public byte[] MainBackup { get; }

        public byte[] AltBackup { get; }

        public byte[] FarBackup { get; }

        public void Dispose()
        {
            Secret.Dispose();
            MainKey.Dispose();
        }
    }
}
