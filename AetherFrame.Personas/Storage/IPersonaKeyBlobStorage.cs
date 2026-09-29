using System;

namespace AetherFrame.Personas.Storage;

/// <summary>
/// Where protected key envelopes are kept: a flat set of blobs, one per slot, in whatever the plugin
/// decides holds them (its own files; never the plugin configuration and never Dalamud's reliable
/// storage, which keeps copies the plugin cannot delete: docs/networking/NETWORK1.md, system 2). The
/// store never sees a location. The slot is the only name, and slots are random local handles, so no
/// persona identity reaches a name (safeguard 7). No implementation exists in this assembly: the
/// plugin's is one directory of files, and the tests' is a dictionary.
/// <para>
/// The contract: <see cref="WriteNew"/> is atomic and durable, holds exactly the bytes it was given
/// or nothing, and never replaces (a held slot is refused with an exception and stays as it was);
/// <see cref="Read"/> returns exactly what is held, null for nothing, and throws only when the
/// storage itself fails. Nothing here deletes. Called one call at a time, through the store.
/// </para>
/// </summary>
public interface IPersonaKeyBlobStorage
{
    /// <summary>The blob held under <paramref name="slot"/>, as a fresh array the caller owns, or null when none is held.</summary>
    byte[]? Read(PersonaSlotId slot);

    /// <summary>
    /// Holds <paramref name="blob"/> under <paramref name="slot"/>. Once this returns, the blob is
    /// durably held; when it throws, nothing is held under the slot. A slot that already holds a
    /// blob is refused, and that blob is left exactly as it was.
    /// </summary>
    void WriteNew(PersonaSlotId slot, ReadOnlySpan<byte> blob);
}
