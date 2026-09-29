namespace AetherFrame.Personas;

/// <summary>What a codec learned from a backup's container alone, before any secret is used.</summary>
public enum PersonaBackupStatus
{
    /// <summary>A container this build can try to open.</summary>
    Supported,

    /// <summary>A well-formed container of a version this build does not read: made by a newer AetherFrame, or by no AetherFrame at all.</summary>
    UnsupportedVersion,

    /// <summary>Not a backup container.</summary>
    Malformed,
}
