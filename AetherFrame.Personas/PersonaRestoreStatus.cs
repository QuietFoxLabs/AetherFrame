namespace AetherFrame.Personas;

/// <summary>The outcome of <see cref="PersonaManager.RestoreBackup"/>.</summary>
public enum PersonaRestoreStatus
{
    /// <summary>The persona is now held by this installation, under a new slot, not selected.</summary>
    Restored,

    /// <summary>This installation already holds the persona; nothing was changed or overwritten.</summary>
    AlreadyPresent,

    /// <summary>The container is of a version this build does not read. No secret was used.</summary>
    UnsupportedVersion,

    /// <summary>The bytes are not a backup container. No secret was used.</summary>
    Malformed,

    /// <summary>The backup did not open under the secret: a wrong secret or damaged content, not distinguished.</summary>
    CannotOpen,

    /// <summary>The backup opened but held no usable P-256 key.</summary>
    InvalidKey,
}
