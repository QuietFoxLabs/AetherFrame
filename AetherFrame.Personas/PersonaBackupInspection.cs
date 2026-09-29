namespace AetherFrame.Personas;

/// <summary>The result of <see cref="IPersonaBackupCodec.Inspect"/>: the status and, when the container states one, its format version.</summary>
public sealed class PersonaBackupInspection
{
    /// <summary>Creates the result.</summary>
    public PersonaBackupInspection(PersonaBackupStatus status, int formatVersion)
    {
        Status = status;
        FormatVersion = formatVersion;
    }

    /// <summary>The status.</summary>
    public PersonaBackupStatus Status { get; }

    /// <summary>The container's format version, or 0 when it could not be read.</summary>
    public int FormatVersion { get; }

    /// <summary>True when <see cref="Status"/> is <see cref="PersonaBackupStatus.Supported"/>.</summary>
    public bool IsSupported => Status == PersonaBackupStatus.Supported;
}
