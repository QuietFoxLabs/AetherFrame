namespace AetherFrame.Protocol;

/// <summary>
/// Every size limit of the remote protocol in one place (docs/networking/NETWORK0.md, "Resource
/// limits"). The members of this class are enforced by the version 1 codecs on every document
/// written and every document read. The limits that only a server can enforce, because they depend
/// on state a document does not carry, are server policy, not protocol: they are documented in
/// docs/networking/NETWORK0.md, "Resource limits", and nothing here declares or enforces them
/// (decision L6, docs/networking/DecisionRegister.md).
/// </summary>
public static class ProtocolLimits
{
    /// <summary>The largest serialized signed document, in bytes (1 MiB). Longer input is refused before it is parsed.</summary>
    public const int MaxDocumentBytes = 1024 * 1024;

    /// <summary>
    /// The bytes of a signed document that are not payload: magic (4), version (2), type (1),
    /// public key (65), payload length (4) and signature (64).
    /// </summary>
    public const int SignedDocumentOverheadBytes = 4 + 2 + 1 + ProtocolConstants.PublicKeyLength + 4 + ProtocolConstants.SignatureLength;

    /// <summary>The largest payload a signed document can carry, so that the document fits <see cref="MaxDocumentBytes"/>.</summary>
    public const int MaxPayloadBytes = MaxDocumentBytes - SignedDocumentOverheadBytes;

    /// <summary>
    /// The most Unicode scalar values (code points) one text field may hold. Counted in scalar
    /// values, not UTF-16 code units or bytes, so the limit means the same in every language.
    /// </summary>
    public const int MaxTextScalars = 32_000;

    /// <summary>The most bytes one text field can occupy on the wire: <see cref="MaxTextScalars"/> four-byte UTF-8 sequences.</summary>
    public const int MaxTextBytes = MaxTextScalars * 4;

    /// <summary>The most Unicode scalar values a Plate's remote name may hold (decision D4). A name holds at least one.</summary>
    public const int MaxNameScalars = 64;

    /// <summary>The most UTF-8 bytes a Plate's remote name may occupy on the wire (decision D4).</summary>
    public const int MaxNameBytes = 256;

    /// <summary>
    /// The most items a schema 2 layout lists (docs/networking/ProtocolSpecification-v1.md, section
    /// 8.5): above the most a local Plate draws (256 elements and 32 Vignettes of 32 quads).
    /// </summary>
    public const int MaxLayoutItems = 2048;

    /// <summary>The most scalar values one text item of a layout holds: a local text (2,000) with both affixes and their spaces fits.</summary>
    public const int MaxLayoutItemTextScalars = 2048;

    /// <summary>The most scalar values all the text items of one layout hold together.</summary>
    public const int MaxLayoutTextScalars = MaxTextScalars;

    /// <summary>The most bytes of a layout identifier (a font or an art id).</summary>
    public const int MaxLayoutIdentBytes = 96;

    /// <summary>The largest coordinate, in hundredths of a canvas unit (100,000 units, as a local Plate allows); the smallest is its negation.</summary>
    public const int MaxLayoutCoordinate = 10_000_000;

    /// <summary>The largest extent (a width or a height), in hundredths of a canvas unit.</summary>
    public const int MaxLayoutExtent = 10_000_000;

    /// <summary>
    /// The most pixels a layout's images declare together (2^25, about 128 MiB decoded as RGBA), so
    /// a viewer's decoding stays bounded however well the images compress.
    /// </summary>
    public const long MaxLayoutImagePixels = 33_554_432;

    /// <summary>The smallest canvas width or height, in hundredths of a canvas unit (one unit).</summary>
    public const int MinLayoutCanvasExtent = 100;

    /// <summary>The largest canvas width or height, in hundredths of a canvas unit (8,192 units).</summary>
    public const int MaxLayoutCanvasExtent = 819_200;

    /// <summary>The largest angle, in hundredths of a degree; the smallest is its negation.</summary>
    public const int MaxLayoutAngle = 36_000;

    /// <summary>The most images a remote profile references.</summary>
    public const int MaxImagesPerProfile = 8;

    /// <summary>The largest source image, in bytes (8 MiB).</summary>
    public const long MaxImageBytes = 8L * 1024 * 1024;

    /// <summary>The largest width or height of a source image, in pixels.</summary>
    public const int MaxImageDimension = 8192;

    /// <summary>The most pixels (width times height) a source image may have: 20 megapixels.</summary>
    public const long MaxImagePixels = 20_000_000;

    /// <summary>
    /// The most bytes the images of one remote profile may declare in total (40 MiB): the remote
    /// profile payload limit, as far as a document can state it. What a server actually stores is
    /// its own measurement, never this declaration.
    /// </summary>
    public const long MaxProfileImageBytes = 40L * 1024 * 1024;

    /// <summary>
    /// The largest timestamp the protocol carries: 9999-12-31T23:59:59Z in Unix seconds, the last
    /// instant <see cref="System.DateTimeOffset"/> can represent. Timestamps are never negative.
    /// </summary>
    public const long MaxUnixSeconds = 253_402_300_799;
}
