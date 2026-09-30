namespace AetherFrame.Personas;

/// <summary>
/// Why a persona operation was refused. A message that accompanies one of these names the rule,
/// never a label, a persona identity or key bytes.
/// </summary>
public enum PersonaError
{
    /// <summary>A label is empty once trimmed, longer than <see cref="PersonaLabel.MaxLength"/> UTF-16 code units, or contains a control character or an unpaired surrogate.</summary>
    InvalidLabel,

    /// <summary>A local identifier is not its prefix followed by 32 lowercase hex digits, or is all zero.</summary>
    InvalidIdentifier,

    /// <summary>A key is not a consistent named-curve P-256 key pair with a private scalar in range, or a key store produced no key or handed out a key that belongs to another persona.</summary>
    InvalidKeyMaterial,

    /// <summary>The persona identity derived from a key is already held by this installation.</summary>
    DuplicateIdentity,

    /// <summary>No persona has the given slot.</summary>
    UnknownPersona,

    /// <summary>The persona's key exists as a record but its store cannot open it now (missing, locked or damaged).</summary>
    KeyUnavailable,

    /// <summary>
    /// A key store could not take a key into custody: its protector or its storage refused, or what
    /// it wrote did not read back as the key it was given. Nothing is held under the slot when the
    /// failure came before the storage accepted the key; after that, the envelope may stay under the
    /// slot, which is never recorded or reused (L12 in docs/networking/DecisionRegister.md).
    /// </summary>
    CustodyFailed,

    /// <summary>A backup container of a version or kind this build cannot open.</summary>
    BackupUnsupported,

    /// <summary>A backup container that is not well formed.</summary>
    BackupMalformed,

    /// <summary>A backup that did not open under the given secret: the secret is wrong or the content is damaged, and the two are not distinguished.</summary>
    BackupCannotBeOpened,

    /// <summary>A backup codec returned no inspection, or one that is not coherent, so the backup was not opened and no secret was used: a codec fault.</summary>
    InvalidBackupInspection,

    /// <summary>
    /// A signer lease was used after the active selection changed (another persona selected, or the
    /// persona deselected): the lease no longer signs, and a new one must be opened for the persona
    /// that is active now. Decision L10 in docs/networking/DecisionRegister.md: a switch revokes
    /// every lease opened before it, and an operation stops rather than sign for a persona that is
    /// no longer active.
    /// </summary>
    LeaseRevoked,

    /// <summary>
    /// The persona registry could not be read: it exists but is not a registry this build reads
    /// (damaged, another version, over its limits), or the storage refused to read it. It is left
    /// exactly as it was, never overwritten, and no manager is made from it (P3 in the decision
    /// register).
    /// </summary>
    RegistryUnreadable,

    /// <summary>
    /// The persona registry could not be saved. Nothing changed in memory. The registry holds either
    /// the state before the change or the state after it, and the next load shows whichever it is.
    /// A key committed just before (a create or a restore) is then held with no record, and is
    /// found and offered for restore as an orphan (L12).
    /// </summary>
    RegistryWriteFailed,

    /// <summary>The registry already holds <see cref="PersonaManager.MaxPersonas"/> personas; nothing was made or committed.</summary>
    RegistryFull,

    /// <summary>
    /// The slot is not a key that can be restored as a persona: a record already names it, no key is
    /// held under it, the key does not open on this account or does not match its envelope, or its
    /// identity is already held by another persona (L12).
    /// </summary>
    NotAnOrphan,
}
