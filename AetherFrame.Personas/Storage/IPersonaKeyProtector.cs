using System;

namespace AetherFrame.Personas.Storage;

/// <summary>
/// How a private scalar is protected at rest, one platform at a time. A protector turns a secret
/// into a blob that only it can open, bound to a context, so that a blob moved under another slot,
/// another public key or another protector does not open. <see cref="ProtectedPersonaKeyStore"/>
/// calls it with a 32-byte scalar and with the envelope's header as the context, and keeps what
/// comes back next to that header. No implementation exists in this assembly: the platform
/// protectors belong to the plugin (docs/networking/DecisionRegister.md, K2 for native Windows, K9
/// elsewhere), and the tests use a fake that protects nothing and says so. Nothing about this
/// interface protects a key. Only a reviewed implementation may claim that, and what it claims is
/// written in the register.
/// <para>
/// An implementation is called only through the store, one call at a time, on whatever thread the
/// store's caller uses. It never keeps the secret, the blob or the context it was given, never
/// logs them, and never prompts.
/// </para>
/// </summary>
public interface IPersonaKeyProtector
{
    /// <summary>
    /// Names the protector and its blob format, and is written into every envelope: a store opens an
    /// envelope only with the protector of the same id. Lowercase letters, digits, dots and hyphens,
    /// 1 to 64 characters, versioned (for example <c>windows.dpapi.currentuser.v1</c>): a change of
    /// format is a new id, never a new meaning for an old one.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// The protected form of <paramref name="secret"/>, bound to <paramref name="context"/>: a blob
    /// that <see cref="Unprotect"/> opens only under the same context. Never null and never empty;
    /// throws when the platform refuses.
    /// </summary>
    byte[] Protect(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> context);

    /// <summary>
    /// The secret that <paramref name="blob"/> holds when this protector made it under the same
    /// <paramref name="context"/>, or null when it cannot open it: another context, another account
    /// or machine, damage, or a blob it did not make. Unavailability is null, never an exception.
    /// The caller owns the array and zeroes it as soon as it is done.
    /// </summary>
    byte[]? Unprotect(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> context);
}
