using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Signing;

namespace AetherFrame.Personas;

/// <summary>
/// The personas this installation holds and which one of them is active, in memory. It is the
/// model behind decision D3 (docs/networking/DecisionRegister.md): any number of independent
/// personas; one active at a time, chosen by the player and changed only by <see cref="Select"/>
/// and <see cref="Deselect"/>; nothing selected on the player's behalf, not even the first persona
/// created; and no input from which a character, an account, a Content ID or a Plate could reach
/// it, because there is no such parameter anywhere. It keeps records only: private keys live behind
/// <see cref="IPersonaKeyStore"/>, and a backup goes through <see cref="IPersonaBackupCodec"/> only
/// when a method here is called for it.
/// <para>
/// A key is committed to the store only after it has been checked: a new or restored key's identity
/// is compared with every persona held before <see cref="IPersonaKeyStore.AddKey"/> is called, and
/// nothing can refuse the operation after that call returns. So a refused create or restore never
/// leaves a key in the store without a record, and nothing here ever deletes a key or a persona.
/// </para>
/// <para>
/// Nothing here is persisted or loaded: how personas would be written to disk, in what schema and
/// under what protection, is not decided (K2, K3), and this type takes no position on it. Every
/// member is safe to call from any thread: the records, the selection, every store call and every
/// call to a store's signer (signing and disposal) are under one lock, so a store and its signers
/// are never called concurrently. The price is that every member, listings included, waits while a
/// store call runs (see <see cref="IPersonaKeyStore"/>). Codec calls run outside that lock, so a slow
/// key derivation never stalls a listing; a codec is a function of its inputs and must tolerate that.
/// </para>
/// </summary>
public sealed class PersonaManager
{
    private readonly Lock gate = new();
    private readonly IPersonaKeyStore keys;
    private readonly IPersonaBackupCodec backups;
    private readonly List<PersonaRecord> personas = new();
    private PersonaSlotId activeSlot;

    // Counts changes of the active selection. A lease records the value it was opened under and signs
    // only while it is unchanged, so no lease outlives a switch.
    private long selection;

    /// <summary>A manager holding no personas yet.</summary>
    public PersonaManager(IPersonaKeyStore keys, IPersonaBackupCodec backups)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(backups);
        this.keys = keys;
        this.backups = backups;
    }

    /// <summary>Every persona, in the order they were created or restored, as a snapshot. No secret is reachable from a record.</summary>
    public IReadOnlyList<PersonaRecord> Personas
    {
        get
        {
            lock (gate)
            {
                return personas.ToArray();
            }
        }
    }

    /// <summary>The active persona, or null when none is selected.</summary>
    public PersonaRecord? Active
    {
        get
        {
            lock (gate)
            {
                return Find(activeSlot);
            }
        }
    }

    /// <summary>
    /// Creates a persona: a fresh key from the store, checked, then committed under a fresh slot,
    /// with the given label (provisional, D9a: see <see cref="PersonaLabel"/>). The new persona is
    /// not selected. The label is checked before any key is made, and the key's identity before the
    /// store commits it, so a refusal leaves the store as it was.
    /// </summary>
    /// <exception cref="PersonaException">
    /// <see cref="PersonaError.InvalidLabel"/>; <see cref="PersonaError.DuplicateIdentity"/> when the
    /// store produced a key whose identity is already held, which no correct store does; or
    /// <see cref="PersonaError.InvalidKeyMaterial"/> when the store produced no key.
    /// </exception>
    public PersonaRecord Create(string label)
    {
        var normalized = PersonaLabel.Normalize(label);
        lock (gate)
        {
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
    /// operation never signs for a persona that is no longer active (see <see cref="PersonaSignerLease"/>;
    /// an interim, fail-closed policy awaiting an owner decision).
    /// </summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.UnknownPersona"/>; the selection is unchanged then.</exception>
    public PersonaRecord Select(PersonaSlotId slot)
    {
        lock (gate)
        {
            var record = Find(slot) ?? throw Unknown();
            if (activeSlot != slot)
            {
                activeSlot = slot;
                selection++;
            }

            return record;
        }
    }

    /// <summary>Leaves no persona active, and revokes every lease opened before (the same interim policy as <see cref="Select"/>).</summary>
    public void Deselect()
    {
        lock (gate)
        {
            if (!activeSlot.IsEmpty)
            {
                activeSlot = default;
                selection++;
            }
        }
    }

    /// <summary>Gives the persona a new label (provisional, D9a). Its slot, key and identity are unchanged, as is the selection.</summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.InvalidLabel"/> or <see cref="PersonaError.UnknownPersona"/>.</exception>
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

            var renamed = personas[index].WithLabel(normalized);
            personas[index] = renamed;
            return renamed;
        }
    }

    /// <summary>The persona with <paramref name="slot"/>, if this installation holds one.</summary>
    public bool TryGet(PersonaSlotId slot, out PersonaRecord? persona)
    {
        lock (gate)
        {
            persona = Find(slot);
            return persona is not null;
        }
    }

    /// <summary>
    /// Opens a signer for the active persona. The lease is the caller's to dispose after one
    /// operation, and it signs only while that persona stays selected. Nothing is selected or created
    /// to satisfy the call: without an active persona, or with one whose key the store cannot open
    /// now, the answer says so and <paramref name="lease"/> is null.
    /// </summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.InvalidKeyMaterial"/> when the store opened a key that is not the active persona's.</exception>
    public PersonaSignerAvailability TryOpenActiveSigner(out PersonaSignerLease? lease)
    {
        lease = null;
        lock (gate)
        {
            var active = Find(activeSlot);
            if (active is null)
            {
                return PersonaSignerAvailability.NoActivePersona;
            }

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
    /// Restores a persona from a backup under the given label (provisional, D9a). The bytes are
    /// copied once, and that private copy is what the codec inspects and then opens, so a caller's
    /// buffer that changes meanwhile cannot change what is opened. The secret is presented to the
    /// codec only when the inspection explicitly reports a supported container; anything else stops
    /// here. A persona this installation already holds is reported as present and left exactly as it
    /// is: no record, label, key or selection changes, and nothing reaches the store. A restored
    /// persona gets a fresh slot and is not selected. The material the codec produced is disposed in
    /// every case. <see cref="PersonaRestoreStatus.UnsupportedVersion"/> and
    /// <see cref="PersonaRestoreStatus.Malformed"/> come only from inspection, before the secret is
    /// used; a refusal from the codec once it has the secret is <see cref="PersonaRestoreStatus.CannotOpen"/>
    /// or <see cref="PersonaRestoreStatus.InvalidKey"/>.
    /// </summary>
    /// <exception cref="PersonaException">
    /// <see cref="PersonaError.InvalidLabel"/>, checked before anything else; or
    /// <see cref="PersonaError.InvalidBackupInspection"/> when the codec gave no coherent inspection.
    /// </exception>
    public PersonaRestoreResult RestoreBackup(ReadOnlySpan<byte> backup, PersonaBackupSecret secret, string label)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var normalized = PersonaLabel.Normalize(label);
        var snapshot = backup.ToArray();
        try
        {
            var inspection = Inspect(snapshot);
            if (inspection.Status != PersonaBackupStatus.Supported)
            {
                var refused = inspection.Status == PersonaBackupStatus.UnsupportedVersion ? PersonaRestoreStatus.UnsupportedVersion : PersonaRestoreStatus.Malformed;
                return new PersonaRestoreResult(refused, null, inspection.FormatVersion);
            }

            PersonaKeyMaterial? material;
            try
            {
                material = backups.Open(snapshot, secret);
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
                    var existing = Find(material.PublicKey.Id);
                    return existing is not null
                        ? new PersonaRestoreResult(PersonaRestoreStatus.AlreadyPresent, existing, inspection.FormatVersion)
                        : new PersonaRestoreResult(PersonaRestoreStatus.Restored, Commit(material, normalized), inspection.FormatVersion);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(snapshot);
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

    /// <summary>
    /// Under the lock, after every check: the store commits a copy of the key under a fresh slot and
    /// the record is added. Nothing after <see cref="IPersonaKeyStore.AddKey"/> can refuse, so the
    /// store never holds a key the manager has no record for. When the store throws, it holds nothing
    /// under the slot (its contract), no record is added, and the exception is the caller's.
    /// </summary>
    private PersonaRecord Commit(PersonaKeyMaterial material, string label)
    {
        var slot = NewSlot();
        keys.AddKey(slot, material);
        var record = new PersonaRecord(slot, material.PublicKey, label);
        personas.Add(record);
        return record;
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
}
