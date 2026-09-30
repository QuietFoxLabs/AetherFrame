using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;

namespace AetherFrame.Services.Network.Publishing;

/// <summary>
/// One signed revision waiting in a persona's outbox, as read back and checked: the snapshot the
/// document says, the document's exact signed bytes, and the prepared images it declares, in the
/// snapshot's order of <see cref="ProfileLayoutSnapshot.Images"/>.
/// </summary>
internal sealed class OutboxEntry
{
    internal OutboxEntry(ProfileLayoutSnapshot snapshot, byte[] document, IReadOnlyList<byte[]> images)
    {
        Snapshot = snapshot;
        Document = document;
        Images = images;
    }

    /// <summary>What the document says, verified as the persona's.</summary>
    internal ProfileLayoutSnapshot Snapshot { get; }

    /// <summary>The document exactly as signed: ECDSA signatures are randomized, so only these bytes are this revision (N2).</summary>
    internal ReadOnlyMemory<byte> Document { get; }

    /// <summary>Each declared image's prepared copy, in the snapshot's order.</summary>
    internal IReadOnlyList<byte[]> Images { get; }
}

/// <summary>
/// An outbox entry's bytes (decision N2, and N2-6's design, sections 2 and 9), big-endian
/// throughout: one signed revision of a profile, exactly as signed, with the prepared copy of
/// every image it declares, in its order:
/// <code>
/// magic "AFPO" | version u16 = 1 | documentLength u32 (1..1,048,576) | document | imageCount u8 (0..8) | images | sha256[32] of everything before it
/// image = length u32 (1..8,388,608) | bytes
/// </code>
/// The largest entry is 42,991,691 bytes (about 41 MiB). The checksum guards against corruption
/// only. Decoding refuses anything but exactly this layout, and never repairs: the magic and the
/// version first, then the length, the checksum, and the parts in order. The document must verify
/// through the protocol library as signed by the persona whose outbox holds it, and be a layout
/// snapshot: this build keeps nothing else in an outbox. Each image must be exactly the image it
/// is declared to be, by section 13, rule 7 (its length, its digest, section 8.2.1's rules, its
/// format and size), and hold exactly what preparation leaves in a copy, with nothing a copy never
/// holds (D5's inventory). Every entry is encoded and decoded again before it is written.
/// </summary>
internal static class OutboxEntryCodec
{
    /// <summary>The one version this build writes and reads.</summary>
    internal const ushort Version = 1;

    private const int HeaderLength = 4 + 2;
    private const int ChecksumLength = 32;

    /// <summary>The largest entry: the largest document, and 8 images of 40 MiB in all.</summary>
    internal const long MaxBytes = HeaderLength + 4 + ProtocolLimits.MaxDocumentBytes + 1
        + (ProtocolLimits.MaxImagesPerProfile * 4L) + ProtocolLimits.MaxProfileImageBytes + ChecksumLength;

    private static ReadOnlySpan<byte> Magic => "AFPO"u8;

    /// <summary>
    /// The entry holding <paramref name="document"/>, signed by <paramref name="key"/>'s persona, and
    /// <paramref name="images"/>, its prepared copies in the snapshot's order; checked by decoding it
    /// again, so what is written is what a later load accepts.
    /// </summary>
    /// <exception cref="PublicationFileException">What was given isn't an entry this build would read back.</exception>
    internal static byte[] Encode(ReadOnlySpan<byte> document, IReadOnlyList<ReadOnlyMemory<byte>> images, PersonaPublicKey key)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(key);
        if (document.IsEmpty || document.Length > ProtocolLimits.MaxDocumentBytes || images.Count > ProtocolLimits.MaxImagesPerProfile)
        {
            throw Damaged("The entry's document or images are outside what an entry holds.");
        }

        long length = HeaderLength + 4 + document.Length + 1 + ChecksumLength;
        for (var index = 0; index < images.Count; index++)
        {
            if (images[index].IsEmpty || images[index].Length > ProtocolLimits.MaxImageBytes)
            {
                throw Damaged("An entry's image is outside what an entry holds.");
            }

            length += 4 + images[index].Length;
        }

        if (length > MaxBytes)
        {
            throw Damaged("The entry is larger than any entry this build reads.");
        }

        var bytes = new byte[length];
        var span = bytes.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], Version);
        var offset = HeaderLength;
        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], (uint)document.Length);
        offset += 4;
        document.CopyTo(span[offset..]);
        offset += document.Length;
        span[offset++] = (byte)images.Count;
        for (var index = 0; index < images.Count; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span[offset..], (uint)images[index].Length);
            offset += 4;
            images[index].Span.CopyTo(span[offset..]);
            offset += images[index].Length;
        }

        SHA256.HashData(span[..offset], span[offset..]);

        // The reader decides what an entry is: bytes a load would refuse are never written.
        Decode(bytes, key);
        return bytes;
    }

    /// <summary>The entry <paramref name="bytes"/> hold, as <paramref name="key"/>'s persona signed it, refusing anything else.</summary>
    /// <exception cref="PublicationFileException">
    /// <see cref="PublicationFileProblem.NewerVersion"/> for a later version, and
    /// <see cref="PublicationFileProblem.Damaged"/> for everything else refused, including a document
    /// that doesn't verify as that persona's.
    /// </exception>
    internal static OutboxEntry Decode(ReadOnlySpan<byte> bytes, PersonaPublicKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (bytes.Length > MaxBytes)
        {
            throw Damaged("The entry is larger than any entry this build reads.");
        }

        if (bytes.Length < HeaderLength || !bytes[..4].SequenceEqual(Magic))
        {
            throw Damaged("The file isn't an outbox entry.");
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        if (version > Version)
        {
            throw new PublicationFileException(PublicationFileProblem.NewerVersion, "The entry was written by a newer AetherFrame.");
        }

        if (version != Version)
        {
            throw Damaged("The entry names a version that never existed.");
        }

        if (bytes.Length < HeaderLength + 4 + 1 + ChecksumLength)
        {
            throw Damaged("The entry is shorter than its parts.");
        }

        var body = bytes[..^ChecksumLength];
        Span<byte> digest = stackalloc byte[ChecksumLength];
        SHA256.HashData(body, digest);
        if (!digest.SequenceEqual(bytes[^ChecksumLength..]))
        {
            throw Damaged("The entry's checksum doesn't match: it is damaged.");
        }

        var offset = HeaderLength;
        var documentLength = BinaryPrimitives.ReadUInt32BigEndian(body[offset..]);
        offset += 4;
        if (documentLength == 0 || documentLength > ProtocolLimits.MaxDocumentBytes || documentLength > (uint)(body.Length - offset - 1))
        {
            throw Damaged("The entry's document length is outside what it holds.");
        }

        var document = body.Slice(offset, (int)documentLength).ToArray();
        offset += (int)documentLength;
        var snapshot = VerifiedSnapshot(document, key);

        var count = body[offset++];
        if (count != snapshot.Images.Count)
        {
            throw Damaged("The entry holds another number of images than its document declares.");
        }

        var images = new byte[count][];
        for (var index = 0; index < count; index++)
        {
            if (body.Length - offset < 4)
            {
                throw Damaged("The entry ends inside an image's length.");
            }

            var imageLength = BinaryPrimitives.ReadUInt32BigEndian(body[offset..]);
            offset += 4;
            if (imageLength == 0 || imageLength > ProtocolLimits.MaxImageBytes || imageLength > (uint)(body.Length - offset))
            {
                throw Damaged("An image's length is outside what the entry holds.");
            }

            images[index] = body.Slice(offset, (int)imageLength).ToArray();
            offset += (int)imageLength;
            CheckImage(images[index], snapshot.Images[index]);
        }

        if (offset != body.Length)
        {
            throw Damaged("Bytes follow the entry's last image.");
        }

        return new OutboxEntry(snapshot, document, images);
    }

    private static ProfileLayoutSnapshot VerifiedSnapshot(byte[] document, PersonaPublicKey key)
    {
        VerifiedDocument verified;
        try
        {
            verified = SignedDocumentCodec.Verify(document);
        }
        catch (ProtocolException e)
        {
            throw new PublicationFileException(PublicationFileProblem.Damaged, "The entry's document doesn't verify.", e);
        }

        if (!verified.PublicKey.Equals(key))
        {
            throw Damaged("The entry's document is signed by another persona.");
        }

        return verified.Document as ProfileLayoutSnapshot ?? throw Damaged("The entry's document isn't a layout snapshot.");
    }

    /// <summary>Section 13, rule 7's check of an image against its declaration, then preparation's inventory (D5): a copy holds nothing it would have removed.</summary>
    private static void CheckImage(byte[] image, ImageReference declared)
    {
        try
        {
            ImageSniffer.CheckDeclared(image, declared);
        }
        catch (ProtocolException e)
        {
            throw new PublicationFileException(PublicationFileProblem.Damaged, "An image isn't the image its document declares.", e);
        }

        if (!ReferenceEquals(PreparedContainer.Clean(image, declared.Format), image))
        {
            throw Damaged("An image holds something a prepared copy never does.");
        }
    }

    private static PublicationFileException Damaged(string message) => new(PublicationFileProblem.Damaged, message);
}
