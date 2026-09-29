namespace AetherFrame.Personas;

/// <summary>The outcome of <see cref="PersonaManager.RestoreBackup"/>.</summary>
public enum PersonaRestoreStatus
{
    /// <summary>The persona is now held by this installation, under a new slot, not selected.</summary>
    Restored,

    /// <summary>This installation already holds the persona; nothing was changed or overwritten.</summary>
    AlreadyPresent,

    /// <summary>Inspection found a container of a version this build does not read. No secret was used.</summary>
    UnsupportedVersion,

    /// <summary>Inspection found that the bytes are not a backup container. No secret was used.</summary>
    Malformed,

    /// <summary>
    /// The backup was inspected as supported and presented to the codec with the secret, and did not
    /// open: a wrong secret, damaged content, or content the codec found unsupported or malformed only
    /// once it opened it. These are not distinguished.
    /// </summary>
    CannotOpen,

    /// <summary>The backup opened under the secret but held no usable P-256 key pair.</summary>
    InvalidKey,
}
