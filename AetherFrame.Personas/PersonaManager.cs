using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using AetherFrame.Personas.Storage;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Personas;

/// <summary>
/// The personas this installation holds and which one of them is active. It is the model behind
/// decision D3 (docs/networking/DecisionRegister.md): any number of independent personas; one
/// active at a time, chosen by the player and changed only by <see cref="Select"/> and
/// <see cref="Deselect"/>; nothing selected on the player's behalf, not even the first persona
/// created; and no input from which a character, an account or a Content ID could reach it,
/// because there is no such parameter anywhere. It keeps records only: private keys live behind
/// <see cref="IPersonaKeyStore"/>, and a backup goes through <see cref="IPersonaBackupCodec"/> only
/// when a method here is called for it.
/// <para>
/// A key is committed to the store only after everything that can be decided in advance has been
/// checked: the label, the registry's room (<see cref="MaxPersonas"/>), the key's identity against
/// every persona held, and the registry that would result. So a refused create or restore never
/// leaves a key in the store; after the commit, only the registry's own save can fail (below).
/// Nothing here ever deletes a key or a persona.
/// </para>
/// <para>
/// A manager made by <see cref="Load"/> keeps its records and its selection in a registry (decision
/// P3): every change is saved first and applied in memory only once the save succeeded, so memory
/// never shows a change the registry has not accepted. A failed save is not applied, and is
/// reported as <see cref="PersonaError.RegistryWriteFailed"/>; when its outcome is unknown (the
/// storage held the new bytes, then failed), a restart may show the change after all. A key
/// committed just before a failed save is held with no record, and is offered for restore by
/// <see cref="Audit"/> and <see cref="RestoreOrphan"/> (decision L12). A manager made by the
/// constructor keeps nothing: it is for tests and for callers that hold no registry.
/// </para>
/// <para>
/// Every member is safe to call from any thread. Changes, store calls and every call to a store's
/// signer (signing and disposal) are under one lock, so a store and its signers are never called
/// concurrently. Listings (<see cref="Personas"/>, <see cref="Active"/>, <see cref="TryGet"/>) read
/// an immutable snapshot published after each change, without the lock, so they never wait on a
/// key write, a protector or a registry save. Codec calls run outside the lock, so a slow key
/// derivation stalls nothing; a codec is a function of its inputs and must tolerate that.
/// </para>
/// </summary>
public sealed class PersonaManager
{
    /// <summary>The most personas one installation holds: the registry's limit.</summary>
    public const int MaxPersonas = 256;

    /// <summary>The largest registry an <see cref="IPersonaRegistryStorage"/> ever holds, in bytes.</summary>
    public const int MaxRegistryBytes = PersonaRegistryCodec.MaxBytes;

    private readonly Lock gate = new();
    private readonly IPersonaKeyStore keys;
    private readonly IPersonaBackupCodec backups;
    private readonly IPersonaRegistryStorage? registry;
    private readonly List<PersonaRecord> personas = new();
    private PersonaSlotId activeSlot;

    // What listings read: replaced whole, under the lock, after every change.
    private volatile Snapshot snapshot = Snapshot.Empty;

    // Counts changes of the active selection. A lease records the value it was opened under and signs
    // only while it is unchanged, so no lease outlives a switch.
    private long selection;

    /// <summary>A manager holding no personas yet, and keeping nothing: it has no registry.</summary>
    public PersonaManager(IPersonaKeyStore keys, IPersonaBackupCodec backups)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(backups);
        this.keys = keys;
        this.backups = backups;
    }

    private PersonaManager(IPersonaKeyStore keys, IPersonaBackupCodec backups, IPersonaRegistryStorage registry)
        : this(keys, backups)
    {
        this.registry = registry;
    }

    /// <summary>Every persona, in the order they were created or restored, as a snapshot. No secret is reachable from a record.</summary>
    public IReadOnlyList<PersonaRecord> Personas => (PersonaRecord[])snapshot.Records.Clone();

    /// <summary>The active persona, or null when none is selected.</summary>
    public PersonaRecord? Active => snapshot.Active;

    /// <summary>
    /// The manager the registry describes (decision P3): its records and its selection, as the
    /// player last left them. A registry that does not exist yet is a first run: no personas, none
    /// selected. A registry that exists but cannot be read, or is not one this build reads, is
    /// refused and left exactly as it was: never overwritten, never replaced, and no manager is made
    /// from it, so the caller turns persona features off and names where the registry is. A
    /// registry a newer AetherFrame wrote is refused the same way, but told apart, so the caller can
    /// say to update rather than to move a damaged file aside.
    /// </summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.RegistryUnreadable"/> or <see cref="PersonaError.RegistryNewerVersion"/>.</exception>
    public static PersonaManager Load(IPersonaKeyStore keys, IPersonaBackupCodec backups, IPersonaRegistryStorage registry)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(registry);
        byte[]? bytes;
        try
        {
            bytes = registry.Read();
        }
        catch (Exception e)
        {
            throw new PersonaException(PersonaError.RegistryUnreadable, "The persona registry could not be read, and is left as it was.", e);
        }

        var manager = new PersonaManager(keys, backups, registry);
        if (bytes is null)
        {
            return manager;
        }

        if (!PersonaRegistryCodec.TryDecode(bytes, out var records, out var active, out var reason, out var newerVersion))
        {
            throw newerVersion
                ? new PersonaException(PersonaError.RegistryNewerVersion, "The persona registry was written by a newer version of AetherFrame (" + reason + "), and is left as it was.")
                : new PersonaException(PersonaError.RegistryUnreadable, "The persona registry is not one this build reads (" + reason + "), and is left as it was.");
        }

        lock (manager.gate)
        {
            manager.personas.AddRange(records);
            manager.activeSlot = active;
            manager.Publish();
        }

        return manager;
    }

    /// <summary>
    /// Creates a persona: a fresh key from the store, checked, then committed under a fresh slot,
    /// with the given label (<see cref="PersonaLabel"/>). The new persona is not selected, and has
    /// not acknowledged K4. The label, the room and the key's identity are checked, and the registry
    /// that would result is encoded, before the store commits anything, so a refusal leaves the store
    /// as it was.
    /// </summary>
    /// <exception cref="PersonaException">
    /// <see cref="PersonaError.InvalidLabel"/>; <see cref="PersonaError.RegistryFull"/>;
    /// <see cref="PersonaError.DuplicateIdentity"/> when the store produced a key whose identity is
    /// already held, which no correct store does; <see cref="PersonaError.InvalidKeyMaterial"/> when
    /// the store produced no key; whatever the store's <see cref="IPersonaKeyStore.AddKey"/> throws
    /// (for the key store core, <see cref="PersonaError.CustodyFailed"/>), in which case no record is
    /// added; or <see cref="PersonaError.RegistryWriteFailed"/>, in which case the key is held with no
    /// record and <see cref="Audit"/> offers it for restore.
    /// </exception>
    public PersonaRecord Create(string label)
    {
        var normalized = PersonaLabel.Normalize(label);
        lock (gate)
        {
            EnsureRoom();
            using var material = keys.GenerateKey() ?? throw new PersonaException(PersonaError.InvalidKeyMaterial, "The key store produced no key.");
            if (Find(material.PublicKey.Id) is not null)
            {
                throw new PersonaException(PersonaError.DuplicateIdentity, "The key store produced a key whose identity this installation already holds.");
            }

            return Commit(material, normalized);
        }
    }

    /// <summary>
    /// Makes the persona with <paramref name="slot"/> the active one. Nothing else changes: no
    /// record and no key. Selecting a different persona revokes every lease opened before, so an
    /// operation never signs for a persona that is no longer active (decision L10, see
    /// <see cref="PersonaSignerLease"/>). The selection is saved before it is applied; a failed save
    /// leaves the old selection active and every lease as it was.
    /// </summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.UnknownPersona"/> or <see cref="PersonaError.RegistryWriteFailed"/>; the selection is unchanged then.</exception>
    public PersonaRecord Select(PersonaSlotId slot)
    {
        lock (gate)
        {
            var record = Find(slot) ?? throw Unknown();
            if (activeSlot != slot)
            {
                Save(personas, slot);
                activeSlot = slot;
                selection++;
                Publish();
            }

            return record;
        }
    }

    /// <summary>Leaves no persona active, and revokes every lease opened before (decision L10). Saved before it is applied.</summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.RegistryWriteFailed"/>; the selection is unchanged then.</exception>
    public void Deselect()
    {
        lock (gate)
        {
            if (!activeSlot.IsEmpty)
            {
                Save(personas, default);
                activeSlot = default;
                selection++;
                Publish();
            }
        }
    }

    /// <summary>
    /// Gives the persona a new label. Its slot, key, identity and acknowledgement are unchanged, as
    /// is the selection. Renaming to the label it has changes nothing, and saves nothing.
    /// </summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.InvalidLabel"/>, <see cref="PersonaError.UnknownPersona"/> or <see cref="PersonaError.RegistryWriteFailed"/>.</exception>
    public PersonaRecord Rename(PersonaSlotId slot, string label)
    {
        var normalized = PersonaLabel.Normalize(label);
        lock (gate)
        {
            var index = IndexOf(slot);
            if (index < 0)
            {
                throw Unknown();
            }

            var record = personas[index];
            return string.Equals(record.Label, normalized, StringComparison.Ordinal) ? record : Replace(index, record.WithLabel(normalized));
        }
    }

    /// <summary>
    /// Records that the player has acknowledged, for this persona, that losing its key means never
    /// updating or unpublishing what it published (decision K4). Nothing may publish for a persona
    /// for the first time before this. Acknowledging twice changes nothing.
    /// </summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.UnknownPersona"/> or <see cref="PersonaError.RegistryWriteFailed"/>.</exception>
    public PersonaRecord Acknowledge(PersonaSlotId slot)
    {
        lock (gate)
        {
            var index = IndexOf(slot);
            if (index < 0)
            {
                throw Unknown();
            }

            var record = personas[index];
            return record.Acknowledged ? record : Replace(index, record.WithAcknowledged());
        }
    }

    /// <summary>The persona with <paramref name="slot"/>, if this installation holds one.</summary>
    public bool TryGet(PersonaSlotId slot, out PersonaRecord? persona)
    {
        persona = null;
        if (slot.IsEmpty)
        {
            return false;
        }

        foreach (var record in snapshot.Records)
        {
            if (record.Slot == slot)
            {
                persona = record;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Opens a signer for the active persona, whichever it is. The lease is the caller's to dispose
    /// after one operation, and it signs only while that persona stays selected. An operation that
    /// showed the player a persona uses <see cref="TryOpenSigner"/> instead, so that a switch can
    /// never make it sign for another (decision L10).
    /// </summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.InvalidKeyMaterial"/> when the store opened a key that is not the active persona's.</exception>
    public PersonaSignerAvailability TryOpenActiveSigner(out PersonaSignerLease? lease)
    {
        lease = null;
        lock (gate)
        {
            var active = Find(activeSlot);
            return active is null ? PersonaSignerAvailability.NoActivePersona : OpenFor(active, out lease);
        }
    }

    /// <summary>
    /// Opens a signer for the persona the operation showed the player, and only while it is the
    /// active one (decision L10): <paramref name="expectedSlot"/> with <paramref name="expectedKey"/>.
    /// When another persona is active, or none is, nothing is opened and the answer is
    /// <see cref="PersonaSignerAvailability.ActivePersonaChanged"/>: the operation stops, and the
    /// player retries. The operation must sign once with the lease and dispose it, and must not hold
    /// a lease across I/O or a dialog; nothing here enforces that, so it is the caller's rule.
    /// </summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.InvalidKeyMaterial"/> when the store opened a key that is not the persona's.</exception>
    public PersonaSignerAvailability TryOpenSigner(PersonaSlotId expectedSlot, PersonaPublicKey expectedKey, out PersonaSignerLease? lease)
    {
        ArgumentNullException.ThrowIfNull(expectedKey);
        lease = null;
        lock (gate)
        {
            var active = Find(activeSlot);
            if (active is null || active.Slot != expectedSlot || !active.PublicKey.Equals(expectedKey))
            {
                return PersonaSignerAvailability.ActivePersonaChanged;
            }

            return OpenFor(active, out lease);
        }
    }

    /// <summary>
    /// The portable backup of one persona, protected under <paramref name="secret"/> by the codec:
    /// the only path by which private material leaves the store, taken only when the player asks.
    /// Nothing is exported on creation, on selection or on a schedule. The material opened for it is
    /// disposed whatever the codec does.
    /// </summary>
    /// <exception cref="PersonaException">
    /// <see cref="PersonaError.UnknownPersona"/>, <see cref="PersonaError.KeyUnavailable"/>, or
    /// <see cref="PersonaError.InvalidKeyMaterial"/> when the store opened another persona's key.
    /// </exception>
    public byte[] ExportBackup(PersonaSlotId slot, PersonaBackupSecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        PersonaKeyMaterial material;
        lock (gate)
        {
            var record = Find(slot) ?? throw Unknown();
            material = keys.OpenKey(slot) ?? throw new PersonaException(PersonaError.KeyUnavailable, "The persona's key cannot be opened now.");
            if (!material.PublicKey.Equals(record.PublicKey))
            {
                material.Dispose();
                throw new PersonaException(PersonaError.InvalidKeyMaterial, "The key store opened a key that does not belong to the persona.");
            }
        }

        // The codec works outside the lock: a slow key derivation must not stall a listing.
        using (material)
        {
            return backups.Write(material, secret);
        }
    }

    /// <summary>What the codec can tell about <paramref name="backup"/> without a secret.</summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.InvalidBackupInspection"/> when the codec gave no coherent answer.</exception>
    public PersonaBackupInspection InspectBackup(ReadOnlySpan<byte> backup) => Inspect(backup);

    /// <summary>
    /// Restores a persona from a backup under the given label. The bytes are copied once, and that
    /// private copy is what the codec inspects and then opens, so a caller's buffer that changes
    /// meanwhile cannot change what is opened. The secret is presented to the codec only when the
    /// inspection explicitly reports a supported container; anything else stops here. A persona this
    /// installation already holds is reported as present and left exactly as it is: no record, label,
    /// key or selection changes, and nothing reaches the store. A restored persona gets a fresh slot,
    /// is not selected, and has not acknowledged K4. The material the codec produced is disposed in
    /// every case. <see cref="PersonaRestoreStatus.UnsupportedVersion"/> and
    /// <see cref="PersonaRestoreStatus.Malformed"/> come only from inspection, before the secret is
    /// used; a refusal from the codec once it has the secret is <see cref="PersonaRestoreStatus.CannotOpen"/>
    /// or <see cref="PersonaRestoreStatus.InvalidKey"/>.
    /// </summary>
    /// <exception cref="PersonaException">
    /// <see cref="PersonaError.InvalidLabel"/>, checked before anything else;
    /// <see cref="PersonaError.InvalidBackupInspection"/> when the codec gave no coherent inspection;
    /// <see cref="PersonaError.RegistryFull"/>, before the store commits anything; whatever the store's
    /// <see cref="IPersonaKeyStore.AddKey"/> throws when it commits the restored key (for the key store
    /// core, <see cref="PersonaError.CustodyFailed"/>), in which case no record is added; or
    /// <see cref="PersonaError.RegistryWriteFailed"/>, in which case the key is held with no record and
    /// <see cref="Audit"/> offers it for restore.
    /// </exception>
    public PersonaRestoreResult RestoreBackup(ReadOnlySpan<byte> backup, PersonaBackupSecret secret, string label)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var normalized = PersonaLabel.Normalize(label);
        var copy = backup.ToArray();
        try
        {
            var inspection = Inspect(copy);
            if (inspection.Status != PersonaBackupStatus.Supported)
            {
                var refused = inspection.Status == PersonaBackupStatus.UnsupportedVersion ? PersonaRestoreStatus.UnsupportedVersion : PersonaRestoreStatus.Malformed;
                return new PersonaRestoreResult(refused, null, inspection.FormatVersion);
            }

            PersonaKeyMaterial? material;
            try
            {
                material = backups.Open(copy, secret);
            }
            catch (PersonaException e) when (Outcome(e.Error) is { } refused)
            {
                return new PersonaRestoreResult(refused, null, inspection.FormatVersion);
            }

            if (material is null)
            {
                return new PersonaRestoreResult(PersonaRestoreStatus.InvalidKey, null, inspection.FormatVersion);
            }

            using (material)
            {
                lock (gate)
                {
                    if (Find(material.PublicKey.Id) is { } existing)
                    {
                        return new PersonaRestoreResult(PersonaRestoreStatus.AlreadyPresent, existing, inspection.FormatVersion);
                    }

                    EnsureRoom();
                    return new PersonaRestoreResult(PersonaRestoreStatus.Restored, Commit(material, normalized), inspection.FormatVersion);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    /// <summary>
    /// Compares what the key store holds with the records (decision L12), without opening any key:
    /// keys held under a slot no record names (orphans), and records whose key is missing, unreadable
    /// or names another slot or key (unusable). An orphan's identity is its envelope header's claim,
    /// unverified until <see cref="VerifyOrphan"/> opens the key. A listing that fails is reported,
    /// never thrown. Nothing is repaired, restored or deleted. It reads every key's envelope under
    /// the lock, so the caller runs it off the framework thread.
    /// </summary>
    public PersonaAudit Audit()
    {
        lock (gate)
        {
            var orphans = new List<PersonaOrphanKey>();
            var unusable = new List<PersonaUnusableRecord>();
            PersonaKeyListing? listing = null;
            try
            {
                listing = keys.ListHeld();
            }
            catch (Exception)
            {
                // Reported through ListingFailed; the records are still checked below.
            }

            if (listing is not null)
            {
                var slots = listing.Slots;
                for (var index = 0; index < slots.Count; index++)
                {
                    var slot = slots[index];
                    if (slot.IsEmpty || IndexOf(slot) >= 0)
                    {
                        continue;
                    }

                    var status = Peek(slot, out var claimed);
                    orphans.Add(new PersonaOrphanKey(
                        slot,
                        status switch
                        {
                            PersonaKeyPeekStatus.Held => PersonaKeyStatusOfOrphan.Readable,
                            PersonaKeyPeekStatus.NamesAnotherSlot => PersonaKeyStatusOfOrphan.NamesAnotherSlot,
                            _ => PersonaKeyStatusOfOrphan.Unreadable,
                        },
                        status == PersonaKeyPeekStatus.Held ? claimed : null));
                }
            }

            foreach (var record in personas)
            {
                PersonaUnusableReason? reason = Peek(record.Slot, out var named) switch
                {
                    PersonaKeyPeekStatus.Missing => PersonaUnusableReason.KeyMissing,
                    PersonaKeyPeekStatus.Unreadable => PersonaUnusableReason.KeyUnreadable,
                    PersonaKeyPeekStatus.NamesAnotherSlot => PersonaUnusableReason.KeyNamesAnotherSlot,
                    _ => named is not null && named.Equals(record.PublicKey) ? null : PersonaUnusableReason.KeyNamesAnotherKey,
                };

                if (reason is { } found)
                {
                    unusable.Add(new PersonaUnusableRecord(record, found));
                }
            }

            return new PersonaAudit(orphans, unusable, listing is null, listing?.Skipped ?? 0);
        }
    }

    /// <summary>
    /// The public key of the orphaned key held under <paramref name="slot"/>, proven by opening it
    /// on this account and disposing it at once; null when a record names the slot, or when the key
    /// is missing, does not open or does not match its envelope. No private material leaves the store.
    /// It opens a key through the protector, so the caller runs it off the framework thread.
    /// </summary>
    public PersonaPublicKey? VerifyOrphan(PersonaSlotId slot)
    {
        lock (gate)
        {
            return slot.IsEmpty || IndexOf(slot) >= 0 ? null : keys.OpenPublicKey(slot);
        }
    }

    /// <summary>
    /// Restores the orphaned key held under <paramref name="slot"/> as a persona, with the given
    /// label (decision L12). Everything is checked again now, under the lock: no record names the
    /// slot, the registry has room, the key opens on this account and matches its envelope, and no
    /// persona holds its identity. The record keeps that slot and takes its public key from the key
    /// opened now; it is not selected, and has not acknowledged K4. Only the player asks for this.
    /// </summary>
    /// <exception cref="PersonaException">
    /// <see cref="PersonaError.InvalidLabel"/>, <see cref="PersonaError.NotAnOrphan"/>,
    /// <see cref="PersonaError.RegistryFull"/> or <see cref="PersonaError.RegistryWriteFailed"/>.
    /// </exception>
    public PersonaRecord RestoreOrphan(PersonaSlotId slot, string label)
    {
        var normalized = PersonaLabel.Normalize(label);
        lock (gate)
        {
            if (slot.IsEmpty || IndexOf(slot) >= 0)
            {
                throw new PersonaException(PersonaError.NotAnOrphan, "A record already names that slot, or it is empty.");
            }

            EnsureRoom();
            var publicKey = keys.OpenPublicKey(slot) ?? throw new PersonaException(PersonaError.NotAnOrphan, "No key under that slot opens on this account as the key its envelope names.");
            if (Find(publicKey.Id) is not null)
            {
                throw new PersonaException(PersonaError.NotAnOrphan, "The key's identity is already held by another persona.");
            }

            var record = new PersonaRecord(slot, publicKey, normalized);
            Save(With(record), activeSlot);
            personas.Add(record);
            Publish();
            return record;
        }
    }

    /// <summary>Whether <paramref name="lease"/> has been released.</summary>
    internal bool IsReleased(PersonaSignerLease lease)
    {
        lock (gate)
        {
            return lease.Inner is null;
        }
    }

    /// <summary>
    /// Takes the store's signer out of <paramref name="lease"/>, once, and disposes it, under the lock:
    /// a store's signer is never disposed while the store or another of its signers is being called,
    /// and never while a signature through this lease is in flight.
    /// </summary>
    internal void Release(PersonaSignerLease lease)
    {
        lock (gate)
        {
            var released = lease.Inner;
            lease.Inner = null;
            (released as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// One signature through a lease, under the lock so that no switch can happen between the check
    /// and the signature: the lease is open, its persona is still the selection it was opened under,
    /// the input names that persona, and the store's signer still reports that persona's key.
    /// </summary>
    internal ProtocolSignature SignUnderLease(PersonaSignerLease lease, SigningInput input)
    {
        lock (gate)
        {
            var signer = lease.Inner;
            ObjectDisposedException.ThrowIf(signer is null, lease);
            if (selection != lease.Selection || activeSlot != lease.Persona.Slot)
            {
                throw new PersonaException(PersonaError.LeaseRevoked, "The active persona changed after this lease was opened, so it no longer signs; open a new lease for the active persona.");
            }

            if (!input.PublicKey.Equals(lease.Persona.PublicKey))
            {
                throw new ProtocolException(ProtocolError.InvalidKey, "The signing input names a different persona than this lease.");
            }

            if (signer.PublicKey is not { } reported || !reported.Equals(lease.Persona.PublicKey))
            {
                throw new PersonaException(PersonaError.InvalidKeyMaterial, "The key store's signer no longer reports the lease's persona.");
            }

            return signer.Sign(input);
        }
    }

    /// <summary>
    /// A refusal from <see cref="IPersonaBackupCodec.Open"/>, which has used the secret by then. So none
    /// of them is reported as the inspection-time outcomes that promise no secret was used.
    /// </summary>
    private static PersonaRestoreStatus? Outcome(PersonaError error) => error switch
    {
        PersonaError.BackupUnsupported or PersonaError.BackupMalformed or PersonaError.BackupCannotBeOpened => PersonaRestoreStatus.CannotOpen,
        PersonaError.InvalidKeyMaterial => PersonaRestoreStatus.InvalidKey,
        _ => null,
    };

    private static PersonaException Unknown() => new(PersonaError.UnknownPersona, "No persona has that slot.");

    /// <summary>The codec's inspection, refused unless it is present and coherent.</summary>
    private PersonaBackupInspection Inspect(ReadOnlySpan<byte> backup)
    {
        var inspection = backups.Inspect(backup);
        if (inspection is null || !PersonaBackupInspection.IsCoherent(inspection.Status, inspection.FormatVersion))
        {
            throw new PersonaException(PersonaError.InvalidBackupInspection, "The backup codec gave no coherent inspection, so the backup was not opened.");
        }

        return inspection;
    }

    /// <summary>A lease over <paramref name="active"/>'s key, under the lock the caller holds.</summary>
    private PersonaSignerAvailability OpenFor(PersonaRecord active, out PersonaSignerLease? lease)
    {
        lease = null;
        var signer = keys.OpenSigner(active.Slot);
        if (signer is null)
        {
            return PersonaSignerAvailability.KeyUnavailable;
        }

        // The signer is the manager's until the lease holds it: disposed on every way out,
        // including a store signer whose key cannot even be read.
        try
        {
            if (signer.PublicKey is not { } reported || !reported.Equals(active.PublicKey))
            {
                throw new PersonaException(PersonaError.InvalidKeyMaterial, "The key store opened a key that does not belong to the active persona.");
            }

            lease = new PersonaSignerLease(this, active, signer, selection);
        }
        catch
        {
            (signer as IDisposable)?.Dispose();
            throw;
        }

        return PersonaSignerAvailability.Available;
    }

    /// <summary>
    /// Under the lock, after every check: the registry that would result is encoded first, then the
    /// store commits a copy of the key under a fresh slot, then the registry is saved, and only then
    /// is the record added. When the store throws, nothing changed. When the save throws, the key is
    /// held with no record (an orphan, L12) and nothing changed in memory.
    /// </summary>
    private PersonaRecord Commit(PersonaKeyMaterial material, string label)
    {
        var slot = NewSlot();
        var record = new PersonaRecord(slot, material.PublicKey, label);
        var bytes = Encode(With(record), activeSlot);
        keys.AddKey(slot, material);
        Save(bytes);
        personas.Add(record);
        Publish();
        return record;
    }

    /// <summary>Replaces the record at <paramref name="index"/>: saved first, then applied.</summary>
    private PersonaRecord Replace(int index, PersonaRecord updated)
    {
        var next = personas.ToArray();
        next[index] = updated;
        Save(next, activeSlot);
        personas[index] = updated;
        Publish();
        return updated;
    }

    /// <summary>The records with <paramref name="record"/> appended, as the registry would hold them.</summary>
    private PersonaRecord[] With(PersonaRecord record)
    {
        var next = new PersonaRecord[personas.Count + 1];
        personas.CopyTo(next);
        next[^1] = record;
        return next;
    }

    private void EnsureRoom()
    {
        if (personas.Count >= MaxPersonas)
        {
            throw new PersonaException(PersonaError.RegistryFull, $"This installation already holds {MaxPersonas} personas, the most the registry holds.");
        }
    }

    /// <summary>
    /// The registry's bytes for a state, or none when this manager keeps no registry. Like the
    /// document codecs, it checks its own output: bytes the next load would refuse are never saved.
    /// It runs before anything changes, and before a new key is committed.
    /// </summary>
    private byte[]? Encode(IReadOnlyList<PersonaRecord> records, PersonaSlotId active)
    {
        if (registry is null)
        {
            return null;
        }

        var bytes = PersonaRegistryCodec.Encode(records, active);
        if (!PersonaRegistryCodec.TryDecode(bytes, out _, out _, out var reason))
        {
            throw new InvalidOperationException("The persona registry this change would save does not read back (" + reason + "), so nothing was saved or committed.");
        }

        return bytes;
    }

    private void Save(IReadOnlyList<PersonaRecord> records, PersonaSlotId active) => Save(Encode(records, active));

    /// <summary>Saves the registry; a failure is <see cref="PersonaError.RegistryWriteFailed"/>, and the caller has changed nothing yet.</summary>
    private void Save(byte[]? bytes)
    {
        if (registry is null || bytes is null)
        {
            return;
        }

        try
        {
            registry.Replace(bytes);
        }
        catch (Exception e)
        {
            throw new PersonaException(PersonaError.RegistryWriteFailed, "The persona registry could not be saved, so the change was not applied; if the storage kept it before failing, a restart may show it.", e);
        }
    }

    /// <summary>Publishes what listings read, under the lock the caller holds.</summary>
    private void Publish() => snapshot = new Snapshot(personas.ToArray(), Find(activeSlot));

    /// <summary>What the store's header says, as the store reports it; a store that throws reads as unreadable.</summary>
    private PersonaKeyPeekStatus Peek(PersonaSlotId slot, out PersonaPublicKey? publicKey)
    {
        try
        {
            return keys.PeekPublicKey(slot, out publicKey);
        }
        catch (Exception)
        {
            publicKey = null;
            return PersonaKeyPeekStatus.Unreadable;
        }
    }

    private PersonaSlotId NewSlot()
    {
        PersonaSlotId slot;
        do
        {
            slot = PersonaSlotId.NewId();
        }
        while (IndexOf(slot) >= 0);

        return slot;
    }

    private PersonaRecord? Find(PersonaSlotId slot)
    {
        var index = slot.IsEmpty ? -1 : IndexOf(slot);
        return index < 0 ? null : personas[index];
    }

    private PersonaRecord? Find(PersonaId id)
    {
        foreach (var persona in personas)
        {
            if (persona.Id == id)
            {
                return persona;
            }
        }

        return null;
    }

    private int IndexOf(PersonaSlotId slot)
    {
        for (var index = 0; index < personas.Count; index++)
        {
            if (personas[index].Slot == slot)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>What listings read: the records and the active persona, as one immutable value.</summary>
    private sealed class Snapshot
    {
        internal static readonly Snapshot Empty = new(Array.Empty<PersonaRecord>(), null);

        internal Snapshot(PersonaRecord[] records, PersonaRecord? active)
        {
            Records = records;
            Active = active;
        }

        internal PersonaRecord[] Records { get; }

        internal PersonaRecord? Active { get; }
    }
}
