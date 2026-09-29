namespace AetherFrame.Personas;

/// <summary>What <see cref="PersonaManager.RestoreBackup"/> did.</summary>
public sealed class PersonaRestoreResult
{
    internal PersonaRestoreResult(PersonaRestoreStatus status, PersonaRecord? persona, int formatVersion)
    {
        Status = status;
        Persona = persona;
        FormatVersion = formatVersion;
    }

    /// <summary>The outcome.</summary>
    public PersonaRestoreStatus Status { get; }

    /// <summary>The new record when restored; the existing, untouched record when already present; otherwise null.</summary>
    public PersonaRecord? Persona { get; }

    /// <summary>The container's format version as inspected, or 0 when it could not be read.</summary>
    public int FormatVersion { get; }
}
