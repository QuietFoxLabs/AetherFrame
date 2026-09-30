using System;
using System.Collections.Generic;

namespace AetherFrame.Personas.Storage;

/// <summary>
/// What a key storage holds, as its listing reads it: the slots with a key under them, and how
/// many entries it skipped because they were not a slot's name (a temporary write, a stray entry).
/// A listing says nothing about whether a key opens; that takes the protector. Immutable.
/// </summary>
public sealed class PersonaKeyListing
{
    /// <summary>A listing of <paramref name="slots"/>, having skipped <paramref name="skipped"/> other entries.</summary>
    public PersonaKeyListing(IReadOnlyList<PersonaSlotId> slots, int skipped)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentOutOfRangeException.ThrowIfNegative(skipped);
        var copy = new PersonaSlotId[slots.Count];
        for (var index = 0; index < copy.Length; index++)
        {
            copy[index] = slots[index];
        }

        Slots = copy;
        Skipped = skipped;
    }

    /// <summary>The slots with a key under them.</summary>
    public IReadOnlyList<PersonaSlotId> Slots { get; }

    /// <summary>How many entries were not a slot's name and were skipped.</summary>
    public int Skipped { get; }
}

/// <summary>What a key's envelope header says, read without opening the key.</summary>
public enum PersonaKeyPeekStatus
{
    /// <summary>An envelope this build reads is held under the slot, and its header names that slot.</summary>
    Held,

    /// <summary>No key is held under the slot.</summary>
    Missing,

    /// <summary>
    /// Something is held under the slot, but it is not an envelope this store can open (damaged, not
    /// an envelope, or another protector's), or the storage could not read it.
    /// </summary>
    Unreadable,

    /// <summary>An envelope is held under the slot, but its header names another slot: it was moved or copied there.</summary>
    NamesAnotherSlot,
}
