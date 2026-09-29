using System;

namespace AetherFrame.Personas;

/// <summary>
/// The seam for the portable <c>.afpersona</c> backup (docs/networking/DecisionRegister.md, D2:
/// approved in principle). Nothing implements it in this assembly: the container format, the key
/// derivation, the cipher and the secret policy all await security approval, and until a reviewed
/// codec exists the only implementations are test doubles that carry no key material at all. The
/// shape fixes what any codec must offer: a look at the container that needs no secret, so that an
/// unsupported or malformed file is refused before a secret is typed; a writer that takes material
/// only as <see cref="PersonaKeyMaterial"/>; and a reader that gives material back the same way, so
/// that no plaintext key crosses this boundary as bytes. The manager calls a codec outside its own
/// lock, so an implementation is a function of its inputs, safe to call from several threads.
/// <para>
/// On restore the manager copies the caller's bytes once and gives that one private copy to both
/// <see cref="Inspect"/> and <see cref="Open"/>, so the container a codec opens is the container it
/// inspected. The secret reaches <see cref="Open"/> only after <see cref="Inspect"/> returned a
/// coherent <see cref="PersonaBackupStatus.Supported"/> result; anything else (no result, an
/// undefined status, an incoherent version) stops the restore without a secret being used.
/// </para>
/// </summary>
public interface IPersonaBackupCodec
{
    /// <summary>Reads only what identifies the container. It needs no secret and is never given one.</summary>
    PersonaBackupInspection Inspect(ReadOnlySpan<byte> backup);

    /// <summary>The portable form of <paramref name="material"/>, protected under <paramref name="secret"/>. The material stays the caller's.</summary>
    byte[] Write(PersonaKeyMaterial material, PersonaBackupSecret secret);

    /// <summary>
    /// Opens <paramref name="backup"/> under <paramref name="secret"/>. The caller owns the material.
    /// Because the secret has been used by then, the manager reports every refusal from here as
    /// <see cref="PersonaRestoreStatus.CannotOpen"/>, except a key pair that
    /// <see cref="PersonaKeyMaterial"/> refuses, which is <see cref="PersonaRestoreStatus.InvalidKey"/>.
    /// </summary>
    /// <exception cref="PersonaException">
    /// <see cref="PersonaError.BackupUnsupported"/>, <see cref="PersonaError.BackupMalformed"/>,
    /// <see cref="PersonaError.BackupCannotBeOpened"/> or <see cref="PersonaError.InvalidKeyMaterial"/>.
    /// </exception>
    PersonaKeyMaterial Open(ReadOnlySpan<byte> backup, PersonaBackupSecret secret);
}
