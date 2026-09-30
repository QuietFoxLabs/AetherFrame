using System;

namespace AetherFrame.Personas.Storage;

/// <summary>
/// Where the persona registry lives (decision P3 in docs/networking/DecisionRegister.md): one
/// entry holding the encoded records and the selection, never any private material. The plugin
/// fills it with a file in its own networking directory, replaced atomically and durably; the tests
/// fill it with memory. The manager calls it only under its lock, one call at a time.
/// </summary>
public interface IPersonaRegistryStorage
{
    /// <summary>
    /// The registry's bytes, or null only when there is no registry yet: the entry, or the place it
    /// would live, does not exist. Anything else that stops the read throws, so that it is never
    /// taken for a first run: access denied, a sharing violation, an entry larger than
    /// <see cref="PersonaManager.MaxRegistryBytes"/> (the storage reads at most one byte more and
    /// throws). A temporary entry from an interrupted replace is never read as the registry.
    /// </summary>
    byte[]? Read();

    /// <summary>
    /// Replaces the registry with <paramref name="bytes"/>, atomically and durably: once this
    /// returns, the registry holds exactly these bytes, and would after a power loss. When it throws,
    /// the registry holds either these bytes or the previous ones, never a mix or nothing.
    /// </summary>
    void Replace(ReadOnlySpan<byte> bytes);
}
