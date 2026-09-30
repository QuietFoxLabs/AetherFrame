using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using AetherFrame.Protocol;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using AetherFrame.Protocol.Signing;
using AetherFrame.Services.Network.Publishing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// NETWORK2's outbox entries (N2-6c; decision N2, and N2-6's design, sections 2 and 9): a signed
/// revision exactly as signed, with the prepared copy of every image it declares. An entry is read
/// back only when its document verifies as the persona's and each image is exactly what the
/// document declares and what preparation leaves; nothing is repaired. Signed with ephemeral keys only.
/// </summary>
public sealed class OutboxEntryCodecTests : IDisposable
{
    private const int Checksum = 32;

    private readonly EcdsaPersonaSigner signer = EcdsaPersonaSigner.CreateEphemeral();

    public void Dispose() => signer.Dispose();

    [Fact]
    public void AnEntry_IsExactlyItsLayout_AndReadsBackWithItsDocumentExactlyAsSigned()
    {
        var (document, images, snapshot) = Signed(2);

        var bytes = OutboxEntryCodec.Encode(document, images, signer.PublicKey);

        Assert.Equal("AFPO"u8.ToArray(), bytes[..4]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4)));
        Assert.Equal((uint)document.Length, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(6)));
        Assert.Equal(document, bytes[10..(10 + document.Length)]);
        var offset = 10 + document.Length;
        Assert.Equal(2, bytes[offset++]);
        foreach (var image in images)
        {
            Assert.Equal((uint)image.Length, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)));
            Assert.Equal(image.ToArray(), bytes[(offset + 4)..(offset + 4 + image.Length)]);
            offset += 4 + image.Length;
        }

        Assert.Equal(bytes.Length - Checksum, offset);
        Assert.Equal(SHA256.HashData(bytes.AsSpan(0, offset)), bytes[offset..]);

        var read = OutboxEntryCodec.Decode(bytes, signer.PublicKey);
        Assert.Equal(document, read.Document.ToArray());
        Assert.Equal(images.Select(i => i.ToArray()), read.Images);
        Assert.True(SnapshotComparer.Same(snapshot, read.Snapshot));
    }

    [Fact]
    public void TheLargestEntry_IsAbout41MiB()
    {
        Assert.Equal(42_991_691, OutboxEntryCodec.MaxBytes);
        var refused = Assert.Throws<PublicationFileException>(() => OutboxEntryCodec.Decode(new byte[OutboxEntryCodec.MaxBytes + 1], signer.PublicKey));
        Assert.Equal(PublicationFileProblem.Damaged, refused.Problem);
    }

    [Fact]
    public void AnEntryANewerAetherFrameWrote_IsToldApart()
    {
        var (document, images, _) = Signed(1);
        var bytes = OutboxEntryCodec.Encode(document, images, signer.PublicKey);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 2);
        Assert.Equal(PublicationFileProblem.NewerVersion, Assert.Throws<PublicationFileException>(() => OutboxEntryCodec.Decode(bytes, signer.PublicKey)).Problem);
    }

    [Fact]
    public void EveryDamage_IsRefused_AndNothingIsRepaired()
    {
        var (document, images, _) = Signed(1);
        var good = OutboxEntryCodec.Encode(document, images, signer.PublicKey);
        var countAt = 10 + document.Length;
        var imageAt = countAt + 1 + 4;

        var cases = new Dictionary<string, byte[]>
        {
            ["empty"] = [],
            ["another magic"] = With(good, b => b[0] = (byte)'X', reseal: false),
            ["version 0"] = With(good, b => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), 0), reseal: false),
            ["shorter than its parts"] = good[..20],
            ["a flipped bit"] = With(good, b => b[imageAt + 3] ^= 1, reseal: false),
            ["no document"] = With(good, b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(6), 0)),
            ["a document past the end"] = With(good, b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(6), (uint)(b.Length - 10 - Checksum))),
            ["a document over 1 MiB"] = With(good, b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(6), ProtocolLimits.MaxDocumentBytes + 1)),
            ["a signature that doesn't verify"] = With(good, b => b[countAt - 1] ^= 1),
            ["fewer images than declared"] = With(good, b => b[countAt] = 0),
            ["more images than declared"] = With(good, b => b[countAt] = 2),
            ["an image of no length"] = With(good, b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(countAt + 1), 0)),
            ["an image past the end"] = With(good, b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(countAt + 1), (uint)(b.Length - imageAt))),
            ["an image not as declared"] = With(good, b => b[imageAt + 20] ^= 1),
            ["a byte after the last image"] = Reseal([.. good[..^Checksum], 0, .. new byte[Checksum]]),
        };

        foreach (var (name, bytes) in cases)
        {
            var refused = Assert.Throws<PublicationFileException>(() => OutboxEntryCodec.Decode(bytes, signer.PublicKey));
            Assert.True(refused.Problem == PublicationFileProblem.Damaged, name);
        }
    }

    [Fact]
    public void AnEntry_IsReadOnlyAsItsOwnPersonas()
    {
        var (document, images, _) = Signed(1);
        var bytes = OutboxEntryCodec.Encode(document, images, signer.PublicKey);
        using var other = EcdsaPersonaSigner.CreateEphemeral();

        Assert.Throws<PublicationFileException>(() => OutboxEntryCodec.Decode(bytes, other.PublicKey));
        Assert.Throws<PublicationFileException>(() => OutboxEntryCodec.Encode(document, images, other.PublicKey));
    }

    [Fact]
    public void AnOutboxHoldsLayoutSnapshots_AndNothingElse()
    {
        var retraction = SignedDocumentCodec.Sign(new ProfileRetraction(ProfileId.NewId(), 1_790_000_000), signer);
        Assert.Equal(PublicationFileProblem.Damaged, Assert.Throws<PublicationFileException>(() => OutboxEntryCodec.Encode(retraction, [], signer.PublicKey)).Problem);
    }

    [Fact]
    public void AnImage_HoldsOnlyWhatPreparationLeaves_EvenWhenItsDeclarationMatches()
    {
        // A gamma chunk the encoder writes and preparation removes: the bytes are what the
        // document declares, and still no copy preparation made.
        var withGamma = PreparedPngs.Png(8, 4, extra: ("gAMA", [0x00, 0x00, 0xB1, 0x8F]));
        var bare = PreparedPngs.Png(8, 4);
        foreach (var (png, stored) in new[] { (withGamma, false), (bare, true) })
        {
            var reference = new ImageReference(AssetId.NewId(), SHA256.HashData(png), ImageFormat.Png, png.Length, 8, 4);
            var snapshot = new ProfileLayoutSnapshot(
                ProfileId.NewId(), RevisionId.NewId(), 1_790_000_000, "Shared", 128_000, 72_000, LayoutBackground.None,
                [new LayoutImage(reference.AssetId, new LayoutPoint(0, 0), 800, 400, 0, LayoutImageFit.Stretch, LayoutFlips.None, 255)],
                [reference]);
            var document = SignedDocumentCodec.Sign(snapshot, signer);

            if (stored)
            {
                Assert.Equal(png, OutboxEntryCodec.Decode(OutboxEntryCodec.Encode(document, [png], signer.PublicKey), signer.PublicKey).Images.Single());
            }
            else
            {
                Assert.Throws<PublicationFileException>(() => OutboxEntryCodec.Encode(document, [png], signer.PublicKey));
            }
        }
    }

    [Fact]
    public void Encode_RefusesWhatNoEntryHolds()
    {
        var (document, images, _) = Signed(1);
        Assert.Throws<PublicationFileException>(() => OutboxEntryCodec.Encode([], images, signer.PublicKey));
        Assert.Throws<PublicationFileException>(() => OutboxEntryCodec.Encode(document, [ReadOnlyMemory<byte>.Empty], signer.PublicKey));
        Assert.Throws<PublicationFileException>(() => OutboxEntryCodec.Encode(document, Enumerable.Repeat(images[0], 9).ToList(), signer.PublicKey));
        Assert.Throws<PublicationFileException>(() => OutboxEntryCodec.Encode(document, [new byte[ProtocolLimits.MaxImageBytes + 1]], signer.PublicKey));
        Assert.Throws<PublicationFileException>(() => OutboxEntryCodec.Encode(document, [], signer.PublicKey));
        Assert.Throws<PublicationFileException>(() => OutboxEntryCodec.Encode(document, [images[0], images[0]], signer.PublicKey));
    }

    /// <summary>A candidate with <paramref name="images"/> images, signed: its document, its images in the snapshot's order, and its snapshot.</summary>
    private (byte[] Document, IReadOnlyList<ReadOnlyMemory<byte>> Images, ProfileLayoutSnapshot Snapshot) Signed(int images)
    {
        var plate = AetherFrame.Domain.Plates.PlateFactory.Create(AetherFrame.Domain.Plates.PlateStartingLayout.Blank, Guid.NewGuid(), "Shared", PublicationCandidates.Now);
        for (var index = 0; index < images; index++)
        {
            plate.Elements.Add(new AetherFrame.Domain.Profiles.ImageProfileElement { AssetId = Guid.NewGuid(), Position = new System.Numerics.Vector2(10f * index, 0f), Size = new System.Numerics.Vector2(64f, 32f), ZIndex = index });
        }

        var candidate = PublicationCandidates.Build(plate);
        var snapshot = candidate.ToSnapshot(ProfileId.NewId(), RevisionId.NewId(), 1_790_000_000);
        var ordered = snapshot.Images.Select(declared => candidate.ImageBytes[candidate.Images.ToList().FindIndex(i => i.AssetId == declared.AssetId)]).ToList();
        return (SignedDocumentCodec.Sign(snapshot, signer), ordered, snapshot);
    }

    private static byte[] With(byte[] good, Action<byte[]> change, bool reseal = true)
    {
        var bytes = (byte[])good.Clone();
        change(bytes);
        return reseal ? Reseal(bytes) : bytes;
    }

    private static byte[] Reseal(byte[] bytes)
    {
        SHA256.HashData(bytes.AsSpan(0, bytes.Length - Checksum), bytes.AsSpan(bytes.Length - Checksum));
        return bytes;
    }
}
