using System;
using System.Collections.Generic;
using System.Threading;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Personas;

/// <summary>
/// The personas this installation holds and which one of them is active, in memory. It is the
/// model behind decision D3 (docs/networking/DecisionRegister.md): any number of independent
/// personas; one active at a time, chosen by the player and changed only by <see cref="Select"/>
/// and <see cref="Deselect"/>; nothing selected on the player's behalf, not even the first persona
/// created; and no input from which a character, an account, a Content ID or a Plate could reach
/// it, because there is no such parameter anywhere. It holds records only: private keys live behind
/// <see cref="IPersonaKeyStore"/>, and a backup goes through <see cref="IPersonaBackupCodec"/> only
/// when a method here is called for it.
/// <para>
/// Nothing here is persisted or loaded: how personas would be written to disk, in what schema and
/// under what protection, is not decided (K2, K3), and this type takes no position on it. Nothing
/// here deletes a persona or a key. Every member is safe to call from any thread: the records, the
/// selection and every store call are under one lock, so a store is never called concurrently.
/// Codec calls run outside that lock, so a slow key derivation never stalls a listing; a codec is
/// a function of its inputs and must tolerate that. A lease, once returned, is the caller's and is
/// not serialized.
/// </para>
/// </summary>
public sealed class PersonaManager
{
    private readonly Lock gate = new();
    private readonly IPersonaKeyStore keys;
    private readonly IPersonaBackupCodec backups;
    private readonly List<PersonaRecord> personas = new();
    private PersonaSlotId activeSlot;

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
    /// Creates a persona: a fresh key from the store under a fresh slot, with the given label. The
    /// new persona is not selected. The label is checked before any key is made.
    /// </summary>
    /// <exception cref="PersonaException">
    /// <see cref="PersonaError.InvalidLabel"/>, or <see cref="PersonaError.DuplicateIdentity"/> when
    /// the store produced a key whose identity is already held, which no correct store does.
    /// </exception>
    public PersonaRecord Create(string label)
    {
        var normalized = PersonaLabel.Normalize(label);
        lock (gate)
        {
            var slot = NewSlot();
            var publicKey = keys.CreateKey(slot);
            if (Find(publicKey.Id) is not null)
            {
                throw new PersonaException(PersonaError.DuplicateIdentity, "The key store produced a key whose identity this installation already holds.");
            }

            var record = new PersonaRecord(slot, publicKey, normalized);
            personas.Add(record);
            return record;
        }
    }

    /// <summary>Makes the persona with <paramref name="slot"/> the active one. Nothing else changes: no record, no key, no lease already open.</summary>
    /// <exception cref="PersonaException"><see cref="PersonaError.UnknownPersona"/>; the selection is unchanged then.</exception>
    public PersonaRecord Select(PersonaSlotId slot)
    {
        lock (gate)
        {
            var record = Find(slot) ?? throw Unknown();
            activeSlot = slot;
            return record;
        }
    }

    /// <summary>Leaves no persona active.</summary>
    public void Deselect()
    {
        lock (gate)
        {
            activeSlot = default;
        }
    }

    /// <summary>Gives the persona a new label. Its slot, key and identity are unchanged, as is the selection.</summary>
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
    /// operation. Nothing is selected or created to satisfy the call: without an active persona, or
    /// with one whose key the store cannot open now, the answer says so and <paramref name="lease"/>
    /// is null.
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

            if (!signer.PublicKey.Equals(active.PublicKey))
            {
                (signer as IDisposable)?.Dispose();
                throw new PersonaException(PersonaError.InvalidKeyMaterial, "The key store opened a key that does not belong to the active persona.");
            }

            lease = new PersonaSignerLease(active, signer);
            return PersonaSignerAvailability.Available;
        }
    }

    /// <summary>
    /// The portable backup of one persona, protected under <paramref name="secret"/> by the codec:
    /// the only path by which private material leaves the store, taken only when the player asks.
    /// Nothing is exported on creation, on selection or on a schedule.
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
    public PersonaBackupInspection InspectBackup(ReadOnlySpan<byte> backup) => backups.Inspect(backup);

    /// <summary>
    /// Restores a persona from a backup under the given label. The container is inspected first, and
    /// a secret is presented to the codec only for a supported one. A persona this installation
    /// already holds is reported as present and left exactly as it is: no record, label, key or
    /// selection changes, and the material the codec produced is disposed. A restored persona gets a
    /// fresh slot and is not selected.
    /// </summary>
    /// <exception cref="PersonaException">
    /// <see cref="PersonaError.InvalidLabel"/>, checked before anything else; or
    /// <see cref="PersonaError.InvalidKeyMaterial"/> when the store adopted the key under another identity.
    /// </exception>
    public PersonaRestoreResult RestoreBackup(ReadOnlySpan<byte> backup, PersonaBackupSecret secret, string label)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var normalized = PersonaLabel.Normalize(label);
        var inspection = backups.Inspect(backup);
        switch (inspection.Status)
        {
            case PersonaBackupStatus.UnsupportedVersion:
                return new PersonaRestoreResult(PersonaRestoreStatus.UnsupportedVersion, null, inspection.FormatVersion);
            case PersonaBackupStatus.Malformed:
                return new PersonaRestoreResult(PersonaRestoreStatus.Malformed, null, inspection.FormatVersion);
        }

        PersonaKeyMaterial material;
        try
        {
            material = backups.Open(backup, secret);
        }
        catch (PersonaException e) when (Outcome(e.Error) is { } refused)
        {
            return new PersonaRestoreResult(refused, null, inspection.FormatVersion);
        }

        var restoredKey = material.PublicKey;
        lock (gate)
        {
            var existing = Find(restoredKey.Id);
            if (existing is not null)
            {
                material.Dispose();
                return new PersonaRestoreResult(PersonaRestoreStatus.AlreadyPresent, existing, inspection.FormatVersion);
            }

            var slot = NewSlot();
            PersonaPublicKey adopted;
            try
            {
                adopted = keys.AdoptKey(slot, material);
            }
            catch
            {
                material.Dispose();
                throw;
            }

            if (!adopted.Equals(restoredKey))
            {
                throw new PersonaException(PersonaError.InvalidKeyMaterial, "The key store adopted the key under another identity.");
            }

            var record = new PersonaRecord(slot, adopted, normalized);
            personas.Add(record);
            return new PersonaRestoreResult(PersonaRestoreStatus.Restored, record, inspection.FormatVersion);
        }
    }

    private static PersonaRestoreStatus? Outcome(PersonaError error) => error switch
    {
        PersonaError.BackupUnsupported => PersonaRestoreStatus.UnsupportedVersion,
        PersonaError.BackupMalformed => PersonaRestoreStatus.Malformed,
        PersonaError.BackupCannotBeOpened => PersonaRestoreStatus.CannotOpen,
        PersonaError.InvalidKeyMaterial => PersonaRestoreStatus.InvalidKey,
        _ => null,
    };

    private static PersonaException Unknown() => new(PersonaError.UnknownPersona, "No persona has that slot.");

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
