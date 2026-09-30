using System;
using System.Buffers.Binary;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Remote;

namespace AetherFrame.Protocol.Documents;

/// <summary>Decodes a verified payload as the model its document type and schema version name.</summary>
internal static class PayloadCodec
{
    public static RemoteDocument Decode(DocumentType type, ReadOnlySpan<byte> payload) => type switch
    {
        DocumentType.ProfileSnapshot => DecodeSnapshot(payload),
        DocumentType.ProfileRetraction => ProfileRetraction.Decode(payload),
        _ => throw new ProtocolException(ProtocolError.UnknownDocumentType, $"Document type {ProtocolText.Number((byte)type)} is not known."),
    };

    /// <summary>A snapshot's schema decides its layout: schema 1 (metadata only) or schema 2 (with its layout).</summary>
    private static RemoteDocument DecodeSnapshot(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(payload) == ProfileLayoutSnapshot.SchemaVersion)
        {
            return ProfileLayoutSnapshot.Decode(payload);
        }

        // Schema 1, and every schema this build does not read, which schema 1's decoder refuses.
        return ProfileSnapshot.Decode(payload);
    }
}
