using System;
using System.Text;

namespace AetherFrame.Protocol;

/// <summary>
/// The fixed bytes of protocol version 1: the document and request proof magics, the version number
/// and the domain separation tags. Every value here is part of the wire format
/// (docs/networking/ProtocolSpecification-v1.md) and is fixed for version 1, except that the freeze
/// replaces the draft marker below (the version and both signing tags, the document's and the
/// request proof's) with the final one; a new version gets new tags.
/// <para>
/// Until the owner freezes version 1, this build writes and reads only drafts of it (decision N3,
/// docs/networking/DecisionRegister.md): the version is <see cref="DraftVersionFlag"/> | 1 and both
/// signing tags end in "-draft". A draft can never be read as final or verify as final, and the
/// persona identity derivation is the same for both, so an identity survives the freeze.
/// </para>
/// </summary>
public static class ProtocolConstants
{
    /// <summary>The high bit of a protocol version, set on every draft: the low fifteen bits name the version it drafts.</summary>
    public const ushort DraftVersionFlag = 0x8000;

    /// <summary>The only protocol version this build reads or writes: 0x8001, a draft of version 1 (decision N3).</summary>
    public const ushort ProtocolVersion = DraftVersionFlag | 1;

    /// <summary>The first four bytes of every signed document: ASCII "AFPD" (AetherFrame Protocol Document).</summary>
    public static ReadOnlySpan<byte> DocumentMagic => "AFPD"u8;

    /// <summary>The first four bytes of every request proof: ASCII "AFRQ" (AetherFrame Request).</summary>
    public static ReadOnlySpan<byte> RequestProofMagic => "AFRQ"u8;

    /// <summary>
    /// The first four bytes of every served profile: ASCII "AFSP" (AetherFrame Served Profile). A
    /// served profile is not signed, has no draft marker and is versioned on its own (section 8.6).
    /// </summary>
    public static ReadOnlySpan<byte> ServedProfileMagic => "AFSP"u8;

    /// <summary>
    /// The domain separation tag that starts every document's signing input, so a signature over a
    /// document can never be a valid signature over anything else AetherFrame signs
    /// (docs/networking/ProtocolSpecification-v1.md, sections 5 and 5.1). Written as a one-byte length followed by these ASCII bytes. The "-draft"
    /// suffix (decision N3) keeps a draft signature from ever verifying under the final version's tag.
    /// </summary>
    public static ReadOnlySpan<byte> SignatureDomainTag => "AetherFrame.Protocol.SignedDocument.v1-draft"u8;

    /// <summary>
    /// The domain separation tag that starts the signing input of every request proof
    /// (docs/networking/ProtocolSpecification-v1.md, sections 5.1 and 14): its own signing context,
    /// so a proof's signature never verifies as a document's, or the reverse. Written as a one-byte
    /// length followed by these ASCII bytes, with the draft suffix of decision N3.
    /// </summary>
    public static ReadOnlySpan<byte> RequestProofDomainTag => "AetherFrame.Protocol.RequestProof.v1-draft"u8;

    /// <summary>
    /// The domain separation tag of persona identity derivation (docs/networking/ProtocolSpecification-v1.md,
    /// "Persona identity"). Written as a one-byte length followed by these ASCII bytes.
    /// </summary>
    public static ReadOnlySpan<byte> PersonaIdDomainTag => "AetherFrame.Protocol.PersonaId.v1"u8;

    /// <summary>The key format byte inside persona identity derivation: 0x01 = uncompressed P-256 point.</summary>
    public const byte PersonaKeyFormatUncompressedP256 = 0x01;

    /// <summary>The length of an uncompressed P-256 public key: 0x04, X (32 bytes), Y (32 bytes).</summary>
    public const int PublicKeyLength = 65;

    /// <summary>The length of a signature: r (32 bytes) followed by s (32 bytes), big-endian.</summary>
    public const int SignatureLength = 64;

    /// <summary>The length of an opaque identifier (profile, revision, asset) on the wire.</summary>
    public const int OpaqueIdLength = 16;

    /// <summary>The length of a SHA-256 digest.</summary>
    public const int DigestLength = 32;

    /// <summary>The length of a request challenge a server issues (docs/networking/ProtocolSpecification-v1.md, section 14.2).</summary>
    public const int ChallengeLength = 32;

    /// <summary>
    /// UTF-8 as the protocol uses it: no byte order mark, and invalid input (an unpaired surrogate on
    /// the way out, a malformed sequence on the way in) throws instead of being replaced.
    /// </summary>
    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
