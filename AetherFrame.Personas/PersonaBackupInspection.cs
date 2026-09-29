using System;

namespace AetherFrame.Personas;

/// <summary>
/// The result of <see cref="IPersonaBackupCodec.Inspect"/>: the status and, when the container
/// states one, its format version. A value is always coherent, because the constructor refuses one
/// that is not: the status is one this build defines; a status that says a version was read
/// (<see cref="PersonaBackupStatus.Supported"/>, <see cref="PersonaBackupStatus.UnsupportedVersion"/>)
/// carries a version of 1 or more; and <see cref="PersonaBackupStatus.Malformed"/>, where no version
/// could be read, carries 0. These are rules of this model, not of any file format: the
/// <c>.afpersona</c> container is not designed yet.
/// </summary>
public sealed class PersonaBackupInspection
{
    /// <summary>Creates the result.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An undefined status, or a version that contradicts the status.</exception>
    public PersonaBackupInspection(PersonaBackupStatus status, int formatVersion)
    {
        if (!IsCoherent(status, formatVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), "A backup inspection needs a defined status, a version of 1 or more when a version was read, and 0 when the container is malformed.");
        }

        Status = status;
        FormatVersion = formatVersion;
    }

    /// <summary>The status.</summary>
    public PersonaBackupStatus Status { get; }

    /// <summary>The container's format version: 1 or more when it was read, 0 when the container is malformed.</summary>
    public int FormatVersion { get; }

    /// <summary>True when <see cref="Status"/> is <see cref="PersonaBackupStatus.Supported"/>.</summary>
    public bool IsSupported => Status == PersonaBackupStatus.Supported;

    /// <summary>The constructor's rule, which the manager applies again to whatever a codec returns.</summary>
    internal static bool IsCoherent(PersonaBackupStatus status, int formatVersion) => status switch
    {
        PersonaBackupStatus.Supported or PersonaBackupStatus.UnsupportedVersion => formatVersion >= 1,
        PersonaBackupStatus.Malformed => formatVersion == 0,
        _ => false,
    };
}
