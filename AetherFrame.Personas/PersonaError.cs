namespace AetherFrame.Personas;

/// <summary>
/// Why a persona operation was refused. A message that accompanies one of these names the rule,
/// never a label, a persona identity or key bytes.
/// </summary>
public enum PersonaError
{
    /// <summary>A label is empty once trimmed, longer than <see cref="PersonaLabel.MaxLength"/> UTF-16 code units, or contains a control character.</summary>
    InvalidLabel,

    /// <summary>A local identifier is not its prefix followed by 32 lowercase hex digits, or is all zero.</summary>
    InvalidIdentifier,

    /// <summary>A key is not a named-curve P-256 key with a private half in range, or a key store handed out a key that belongs to another persona.</summary>
    InvalidKeyMaterial,

    /// <summary>The persona identity derived from a key is already held by this installation.</summary>
    DuplicateIdentity,

    /// <summary>No persona has the given slot.</summary>
    UnknownPersona,

    /// <summary>The persona's key exists as a record but its store cannot open it now (missing, locked or damaged).</summary>
    KeyUnavailable,

    /// <summary>A backup container of a version or kind this build cannot open.</summary>
    BackupUnsupported,

    /// <summary>A backup container that is not well formed.</summary>
    BackupMalformed,

    /// <summary>A backup that did not open under the given secret: the secret is wrong or the content is damaged, and the two are not distinguished.</summary>
    BackupCannotBeOpened,
}
