namespace AetherFrame.Personas;

/// <summary>
/// What a codec learned from a backup's container alone, before any secret is used. The values start
/// at 1: 0, the value of a status nobody set, is not a status, so a codec that forgets to set one
/// produces an inspection the manager refuses rather than an accidental <see cref="Supported"/>.
/// </summary>
public enum PersonaBackupStatus
{
    /// <summary>A container this build can try to open.</summary>
    Supported = 1,

    /// <summary>A well-formed container of a version this build does not read: made by a newer AetherFrame, or by no AetherFrame at all.</summary>
    UnsupportedVersion = 2,

    /// <summary>Not a backup container.</summary>
    Malformed = 3,
}
