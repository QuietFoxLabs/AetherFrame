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
        ("get_Personas()", "list", w => _ = w.Manager.Personas),
        ("get_Active()", "read", w => _ = w.Manager.Active),
        ("Create(String)", "succeeds", w => w.Manager.Create("New")),
        ("Create(String)", "invalid label", w => Refused(() => w.Manager.Create(" "))),
        ("Create(String)", "duplicate identity", w =>
        {
            w.Store.NextKey = () => SyntheticKeys.Copy(w.MainKey);
            Refused(() => w.Manager.Create("Duplicate"));
        }),
        ("Create(String)", "store cannot commit", w =>
        {
            w.Store.FailNextAdd = new InvalidOperationException("The store fails.");
            Refused(() => w.Manager.Create("Failing"));
        }),
        ("Rename(PersonaSlotId, String)", "the active persona", w => w.Manager.Rename(w.Main.Slot, "Main renamed")),
        ("Rename(PersonaSlotId, String)", "another persona", w => w.Manager.Rename(w.Alt.Slot, "Alt renamed")),
        ("Rename(PersonaSlotId, String)", "invalid label", w => Refused(() => w.Manager.Rename(w.Main.Slot, ""))),
        ("Rename(PersonaSlotId, String)", "unknown slot", w => Refused(() => w.Manager.Rename(PersonaSlotId.NewId(), "Nobody"))),
        ("TryGet(PersonaSlotId, PersonaRecord&)", "held", w => w.Manager.TryGet(w.Alt.Slot, out _)),
        ("TryGet(PersonaSlotId, PersonaRecord&)", "unknown", w => w.Manager.TryGet(PersonaSlotId.NewId(), out _)),
        ("TryOpenActiveSigner(PersonaSignerLease&)", "open and sign", w =>
        {
            if (w.Manager.TryOpenActiveSigner(out var lease) == PersonaSignerAvailability.Available)
            {
                using (lease)
                {
                    Documents.SignedRetraction(lease!.Signer);
                }
            }
        }),
        ("TryOpenActiveSigner(PersonaSignerLease&)", "key locked", w =>
        {
            w.Store.Lock(w.Main.Slot);
            w.Manager.TryOpenActiveSigner(out _);
            w.Store.Unlock(w.Main.Slot);
        }),
        ("ExportBackup(PersonaSlotId, PersonaBackupSecret)", "succeeds", w => w.Manager.ExportBackup(w.Alt.Slot, w.Secret)),
        ("ExportBackup(PersonaSlotId, PersonaBackupSecret)", "unknown slot", w => Refused(() => w.Manager.ExportBackup(PersonaSlotId.NewId(), w.Secret))),
        ("ExportBackup(PersonaSlotId, PersonaBackupSecret)", "key locked", w =>
        {
            w.Store.Lock(w.Alt.Slot);
            Refused(() => w.Manager.ExportBackup(w.Alt.Slot, w.Secret));
            w.Store.Unlock(w.Alt.Slot);
        }),
        ("InspectBackup(ReadOnlySpan`1)", "supported", w => w.Manager.InspectBackup(w.FarBackup)),
        ("InspectBackup(ReadOnlySpan`1)", "malformed", w => w.Manager.InspectBackup(new byte[3])),
        ("InspectBackup(ReadOnlySpan`1)", "incoherent inspection", w =>
        {
            w.Codec.InspectOverride = _ => null;
            Refused(() => w.Manager.InspectBackup(w.FarBackup));
            w.Codec.InspectOverride = null;
        }),
        ("RestoreBackup(ReadOnlySpan`1, PersonaBackupSecret, String)", "a new persona", w => Assert.Equal(PersonaRestoreStatus.Restored, w.Manager.RestoreBackup(w.FarBackup, w.Secret, "Far").Status)),
        ("RestoreBackup(ReadOnlySpan`1, PersonaBackupSecret, String)", "the active persona again", w => Assert.Equal(PersonaRestoreStatus.AlreadyPresent, w.Manager.RestoreBackup(w.MainBackup, w.Secret, "Main again").Status)),
        ("RestoreBackup(ReadOnlySpan`1, PersonaBackupSecret, String)", "another held persona again", w => Assert.Equal(PersonaRestoreStatus.AlreadyPresent, w.Manager.RestoreBackup(w.AltBackup, w.Secret, "Alt again").Status)),
        ("RestoreBackup(ReadOnlySpan`1, PersonaBackupSecret, String)", "unsupported version", w => Assert.Equal(PersonaRestoreStatus.UnsupportedVersion, w.Manager.RestoreBackup(HandleBackupCodec.WithVersion(w.FarBackup, 7), w.Secret, "Far").Status)),
        ("RestoreBackup(ReadOnlySpan`1, PersonaBackupSecret, String)", "malformed", w => Assert.Equal(PersonaRestoreStatus.Malformed, w.Manager.RestoreBackup(new byte[5], w.Secret, "Far").Status)),
        ("RestoreBackup(ReadOnlySpan`1, PersonaBackupSecret, String)", "wrong secret", w =>
        {
            using var wrong = PersonaBackupSecret.FromText("wrong");
            Assert.Equal(PersonaRestoreStatus.CannotOpen, w.Manager.RestoreBackup(w.FarBackup, wrong, "Far").Status);
        }),
        ("RestoreBackup(ReadOnlySpan`1, PersonaBackupSecret, String)", "store cannot commit", w =>
        {
            w.Store.FailNextAdd = new InvalidOperationException("The store fails.");
            Refused(() => w.Manager.RestoreBackup(w.FarBackup, w.Secret, "Far"));
        }),
        ("RestoreBackup(ReadOnlySpan`1, PersonaBackupSecret, String)", "invalid label", w => Refused(() => w.Manager.RestoreBackup(w.FarBackup, w.Secret, "\n"))),
        ("RestoreBackup(ReadOnlySpan`1, PersonaBackupSecret, String)", "incoherent inspection", w =>
        {
            w.Codec.InspectOverride = _ => HandleBackupCodec.Forged((PersonaBackupStatus)42, 1);
            Refused(() => w.Manager.RestoreBackup(w.FarBackup, w.Secret, "Far"));
            w.Codec.InspectOverride = null;
        }),
        ("Select(PersonaSlotId)", "unknown slot fails", w => Refused(() => w.Manager.Select(PersonaSlotId.NewId()))),
        ("Select(PersonaSlotId)", "empty slot fails", w => Refused(() => w.Manager.Select(default))),
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
        // By full signature, so a new overload of a classified name fails here too.
        var members = typeof(PersonaManager)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(Signature)
            .ToHashSet();
        var classified = OtherOperations.Select(o => o.Member).Append("Select(PersonaSlotId)").Append("Deselect()").ToHashSet();
        Assert.Equal(members.Order(StringComparer.Ordinal), classified.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheManagerAndItsLeases_HaveNoOtherWayIn()
    {
        // No interface (an explicit implementation is private, so the table above would not see it),
        // no base class but object, and no public constructor but the one taking the two seams.
        var manager = typeof(PersonaManager);
        Assert.Empty(manager.GetInterfaces());
        Assert.Equal(typeof(object), manager.BaseType);
        Assert.DoesNotContain(manager.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly), m => m.Name.Contains('.', StringComparison.Ordinal));
        Assert.Equal(["Void .ctor(AetherFrame.Personas.IPersonaKeyStore, AetherFrame.Personas.IPersonaBackupCodec)"], manager.GetConstructors().Select(c => c.ToString()!));

        // A lease holds its manager, so its surface is pinned as well: it can sign and be disposed,
        // and nothing on it selects.
        var lease = typeof(PersonaSignerLease);
        Assert.Equal([typeof(IDisposable)], lease.GetInterfaces());
        Assert.Equal(
            ["Dispose()", "ToString()", "get_Persona()", "get_Signer()"],
            lease.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(Signature).Order(StringComparer.Ordinal));
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
    public async Task ASwitch_WaitsForASignatureInFlight_AndTheLeaseIsRevokedAfterIt()
    {
        // The check and the signature are one step under the manager's lock: a switch the player
        // makes while a signature is running completes only once it is done, so no signature is ever
        // produced for a persona after the switch away from it has returned.
        using var world = new World();
        var blocking = BlockingStoreSigners(world);
        world.Manager.Select(world.Main.Slot);
        world.Manager.TryOpenActiveSigner(out var opened);
        using var lease = opened!;
        var signer = blocking[^1];

        var signing = Task.Run(() => Documents.SignedRetraction(lease.Signer));
        Assert.True(signer.Entered.Wait(TimeSpan.FromSeconds(10)));
        var switching = Task.Run(() => world.Manager.Select(world.Alt.Slot));
        Assert.NotSame(switching, await Task.WhenAny(switching, Task.Delay(300)));
        Assert.False(switching.IsCompleted);

        signer.Release.Set();
        var document = await signing;
        await switching;
        Assert.Equal(world.Main.Id, SignedDocumentCodec.Verify(document).Persona);
        Assert.Equal(world.Alt.Slot, world.Manager.Active!.Slot);
        Assert.Equal(PersonaError.LeaseRevoked, Assert.Throws<PersonaException>(() => Documents.SignedRetraction(lease.Signer)).Error);
    }

    [Fact]
    public async Task DisposingALease_WaitsForItsSignatureInFlight_ThenDisposesTheStoresSigner()
    {
        using var world = new World();
        var blocking = BlockingStoreSigners(world);
        world.Manager.Select(world.Main.Slot);
        world.Manager.TryOpenActiveSigner(out var opened);
        var lease = opened!;
        var signer = blocking[^1];

        var signing = Task.Run(() => Documents.SignedRetraction(lease.Signer));
        Assert.True(signer.Entered.Wait(TimeSpan.FromSeconds(10)));
        var disposing = Task.Run(lease.Dispose);
        Assert.NotSame(disposing, await Task.WhenAny(disposing, Task.Delay(300)));

        signer.Release.Set();
        await signing;
        await disposing;
        Assert.Equal(["sign-start", "sign-end", "dispose"], signer.Events.ToArray());
        Assert.False(signer.DisposedWhileSigning);
    }

    [Fact]
    public async Task AStoresSigner_IsDisposedUnderTheLock_SoTheStoreIsNeverCalledMeanwhile()
    {
        // The store and its signers are never called concurrently, disposal included: while a lease
        // is disposing the store's signer, a create (which calls the store) waits for it.
        using var world = new World();
        var signers = new List<BlockingSigner>();
        world.Store.SignerOverride = slot =>
        {
            var signer = new BlockingSigner(world.Store.Held(slot).CreateSigner()) { BlockDispose = true };
            signers.Add(signer);
            return signer;
        };
        world.Manager.Select(world.Main.Slot);
        world.Manager.TryOpenActiveSigner(out var opened);
        var signer = signers[^1];

        var disposing = Task.Run(opened!.Dispose);
        Assert.True(signer.DisposeEntered.Wait(TimeSpan.FromSeconds(10)));
        var addsBefore = world.Store.CallsTo("AddKey");
        var creating = Task.Run(() => world.Manager.Create("Meanwhile"));
        Assert.NotSame(creating, await Task.WhenAny(creating, Task.Delay(300)));
        Assert.Equal(addsBefore, world.Store.CallsTo("AddKey"));

        signer.DisposeRelease.Set();
        await disposing;
        await creating;
        Assert.Equal(addsBefore + 1, world.Store.CallsTo("AddKey"));
    }

    [Fact]
    public void TryOpenActiveSigner_DisposesAStoreSignerItRefuses()
    {
        using var world = new World();
        var signers = new List<ShiftingSigner>();
        world.Store.SignerOverride = slot =>
        {
            // The store answers for the main persona with the alt's key.
            var signer = new ShiftingSigner(world.Store.Held(world.Alt.Slot).CreateSigner());
            signers.Add(signer);
            return signer;
        };
        world.Manager.Select(world.Main.Slot);
        Assert.Equal(PersonaError.InvalidKeyMaterial, Assert.Throws<PersonaException>(() => world.Manager.TryOpenActiveSigner(out _)).Error);
        Assert.True(Assert.Single(signers).Disposed);
    }

    [Fact]
    public void TryOpenActiveSigner_DisposesAStoreSignerWhoseKeyCannotBeRead()
    {
        using var world = new World();
        ShiftingSigner? signer = null;
        world.Store.SignerOverride = slot => signer = new ShiftingSigner(world.Store.Held(slot).CreateSigner()) { ThrowOnPublicKey = true };
        world.Manager.Select(world.Main.Slot);
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => world.Manager.TryOpenActiveSigner(out _));
        Assert.True(signer!.Disposed);
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

    private static string Signature(MethodInfo method) =>
        $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";

    /// <summary>Makes the store answer every signer request with a <see cref="BlockingSigner"/>, collected in the returned list.</summary>
    private static List<BlockingSigner> BlockingStoreSigners(World world)
    {
        var signers = new List<BlockingSigner>();
        world.Store.SignerOverride = slot =>
        {
            var signer = new BlockingSigner(world.Store.Held(slot).CreateSigner());
            signers.Add(signer);
            return signer;
        };
        return signers;
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
