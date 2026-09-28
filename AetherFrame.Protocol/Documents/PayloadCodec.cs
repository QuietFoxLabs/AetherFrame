using System;
using AetherFrame.Protocol.Encoding;
using AetherFrame.Protocol.Remote;

namespace AetherFrame.Protocol.Documents;

/// <summary>Decodes a verified payload as the model its document type names.</summary>
internal static class PayloadCodec
{
    public static RemoteDocument Decode(DocumentType type, ReadOnlySpan<byte> payload) => type switch
    {
        DocumentType.ProfileSnapshot => ProfileSnapshot.Decode(payload),
        DocumentType.ProfileRetraction => ProfileRetraction.Decode(payload),
        _ => throw new ProtocolException(ProtocolError.UnknownDocumentType, $"Document type {ProtocolText.Number((byte)type)} is not known."),
    };
}
