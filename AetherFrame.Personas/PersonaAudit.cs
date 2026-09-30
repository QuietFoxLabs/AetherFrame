using System;
using System.Collections.Generic;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Personas;

/// <summary>
/// What the key storage holds compared with the registry's records (decision L12 in
/// docs/networking/DecisionRegister.md), read without opening any key: keys no record names
/// (<see cref="Orphans"/>), records whose key is missing or is not theirs (<see cref="Unusable"/>),
/// and whether the listing itself worked. Nothing here was repaired, deleted or restored; each of
/// those is the player's choice. Immutable.
/// </summary>
public sealed class PersonaAudit
{
    internal PersonaAudit(IReadOnlyList<PersonaOrphanKey> orphans, IReadOnlyList<PersonaUnusableRecord> unusable, bool listingFailed, int skippedEntries)
    {
        Orphans = orphans;
        Unusable = unusable;
        ListingFailed = listingFailed;
        SkippedEntries = skippedEntries;
    }

    /// <summary>Keys held under a slot no record names, in the order the storage listed them.</summary>
    public IReadOnlyList<PersonaOrphanKey> Orphans { get; }

    /// <summary>Records whose key cannot be the persona's, in record order.</summary>
    public IReadOnlyList<PersonaUnusableRecord> Unusable { get; }

    /// <summary>
    /// True when the key storage could not be listed: <see cref="Orphans"/> is then empty for want of
    /// a listing, not because none exists. Records are still checked one by one.
    /// </summary>
    public bool ListingFailed { get; }

    /// <summary>How many entries the key storage skipped because they were not a slot's name.</summary>
    public int SkippedEntries { get; }
}

/// <summary>
/// A key held under a slot no record names. Its identity is what its envelope's header claims, which
/// anyone who can write the key files could forge, so it is shown as unverified until
/// <see cref="PersonaManager.VerifyOrphan"/> opens the key and proves it. Immutable.
/// </summary>
public sealed class PersonaOrphanKey
{
    internal PersonaOrphanKey(PersonaSlotId slot, PersonaKeyStatusOfOrphan status, PersonaPublicKey? claimedPublicKey)
    {
        Slot = slot;
        Status = status;
        ClaimedPublicKey = claimedPublicKey;
    }

    /// <summary>The slot the key is held under.</summary>
    public PersonaSlotId Slot { get; }

    /// <summary>What its header says, read without opening it.</summary>
    public PersonaKeyStatusOfOrphan Status { get; }

    /// <summary>The public key the header names, unverified; null when the header could not be read or names another slot.</summary>
    public PersonaPublicKey? ClaimedPublicKey { get; }

    /// <summary>The identity the header claims, unverified; null with <see cref="ClaimedPublicKey"/>.</summary>
    public PersonaId? ClaimedId => ClaimedPublicKey?.Id;

    /// <summary>The slot only, never an identity.</summary>
    public override string ToString() => Slot.ToString();
}

/// <summary>What an orphaned key's header says, read without opening it.</summary>
public enum PersonaKeyStatusOfOrphan
{
    /// <summary>The header names this slot and a public key; whether the key opens is not known yet.</summary>
    Readable,

    /// <summary>What is held is not an envelope this store can open (damaged, not an envelope, or another protector's), or could not be read.</summary>
    Unreadable,

    /// <summary>The header names another slot: the key was moved or copied here, and cannot open under this slot.</summary>
    NamesAnotherSlot,
}

/// <summary>A record whose key cannot be the persona's. Its record stays as it is; nothing is repaired. Immutable.</summary>
public sealed class PersonaUnusableRecord
{
    internal PersonaUnusableRecord(PersonaRecord record, PersonaUnusableReason reason)
    {
        Record = record;
        Reason = reason;
    }

    /// <summary>The record.</summary>
    public PersonaRecord Record { get; }

    /// <summary>Why its key cannot be the persona's.</summary>
    public PersonaUnusableReason Reason { get; }

    /// <summary>The slot only, never an identity.</summary>
    public override string ToString() => Record.Slot + ": " + Reason;
}

/// <summary>Why a record's key cannot be the persona's.</summary>
public enum PersonaUnusableReason
{
    /// <summary>No key is held under the record's slot.</summary>
    KeyMissing,

    /// <summary>What is held under the slot is not an envelope this store can open (damaged, not an envelope, or another protector's), or could not be read.</summary>
    KeyUnreadable,

    /// <summary>The envelope under the slot names another slot.</summary>
    KeyNamesAnotherSlot,

    /// <summary>The envelope under the slot names another public key than the record's.</summary>
    KeyNamesAnotherKey,
}
