using System;
using AetherFrame.Personas;

namespace AetherFrame.Services.Network.Personas;

/// <summary>
/// The backup codec of a build with no backup format: the encrypted backup waits for the D2
/// details and K5 (docs/networking/DecisionRegister.md; NETWORK1 increment 6, stage 2), so nothing
/// here exports or restores a key. Every container reads as not one this build opens, and writing
/// or opening one is refused before anything is used. The persona window offers neither. Compiled
/// only in the networking preview flavour.
/// </summary>
public sealed class NoBackupCodec : IPersonaBackupCodec
{
    private const string Refusal = "This build has no persona backup format yet.";

    /// <inheritdoc />
    public PersonaBackupInspection Inspect(ReadOnlySpan<byte> backup) => new(PersonaBackupStatus.Malformed, 0);

    /// <inheritdoc />
    public byte[] Write(PersonaKeyMaterial material, PersonaBackupSecret secret) =>
        throw new PersonaException(PersonaError.BackupUnsupported, Refusal);

    /// <inheritdoc />
    public PersonaKeyMaterial Open(ReadOnlySpan<byte> backup, PersonaBackupSecret secret) =>
        throw new PersonaException(PersonaError.BackupUnsupported, Refusal);
}
