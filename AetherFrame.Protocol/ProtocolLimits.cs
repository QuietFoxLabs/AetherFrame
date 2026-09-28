namespace AetherFrame.Protocol;

/// <summary>
/// Every size limit of the remote protocol in one place (docs/networking/NETWORK0.md, "Resource
/// limits"). The members of this class are enforced by the version 1 codecs on every document
/// written and every document read. The limits that only a server can enforce, because they depend
/// on state a document does not carry, are declared in <see cref="FuturePolicy"/> and enforced by
/// nothing here.
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

    /// <summary>
    /// Approved product limits that no version 1 document can enforce: they are server policy over
    /// state a document does not carry (how many profiles a persona has published, how much it
    /// stores), or they belong to the layout schema NETWORK1 will define. Declared here so the
    /// numbers have one home; nothing in this assembly checks them.
    /// </summary>
    public static class FuturePolicy
    {
        /// <summary>The most elements a remote profile layout may hold. Reserved for the layout schema; no version 1 document carries elements.</summary>
        public const int MaxElementsPerProfile = 256;

        /// <summary>The most Components a remote profile layout may hold. Reserved for the layout schema; no version 1 document carries Components.</summary>
        public const int MaxComponentsPerProfile = 32;

        /// <summary>The largest width or height of the processed copy a server serves. The server downscales; a document never declares it.</summary>
        public const int MaxProcessedImageDimension = 4096;

        /// <summary>The most profiles one persona may publish. Server state.</summary>
        public const int MaxProfilesPerPersona = 20;

        /// <summary>The most shares one persona may keep active. Server state; sharing is not part of NETWORK0.</summary>
        public const int MaxActiveShares = 10;

        /// <summary>The storage a persona may use in total (250 MiB). Server accounting.</summary>
        public const long PersonaStorageTargetBytes = 250L * 1024 * 1024;

        /// <summary>The storage a persona may use in its first seven days (50 MiB). Server accounting.</summary>
        public const long PersonaFirstWeekStorageTargetBytes = 50L * 1024 * 1024;
    }
}
