using System;
using System.Globalization;
using System.Linq;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Protocol.Tests;

/// <summary>Named snapshot payloads that each break exactly one rule of the schema.</summary>
internal static class SnapshotPayloadCases
{
    // Offsets inside the sample snapshot payload: schema (2) + profileId (16) + revisionId (16) + createdAt (8) = 42, then the name's length.
    private const int NameLengthOffset = 42;

    public static byte[] Build(string name)
    {
        var sortedIds = Enumerable.Range(1, 9).Select(i => AssetId.Parse("ast_" + i.ToString("x32", CultureInfo.InvariantCulture))).ToArray();
        return name switch
        {
            "schema 0" => PayloadBuilder.Snapshot(schema: 0),
            "schema 2" => PayloadBuilder.Snapshot(schema: 2),
            "zero profile id" => PayloadBuilder.Snapshot(profileId: new byte[16]),
            "zero revision id" => PayloadBuilder.Snapshot(revisionId: new byte[16]),
            "createdAt over max" => PayloadBuilder.Snapshot(createdAt: (ulong)ProtocolLimits.MaxUnixSeconds + 1),
            "createdAt u64 max" => PayloadBuilder.Snapshot(createdAt: ulong.MaxValue),
            "name with NUL" => PayloadBuilder.Snapshot(nameBytes: [0x61, 0x00]),
            "name invalid utf8" => PayloadBuilder.Snapshot(nameBytes: [0xFF, 0xFE]),
            "name overlong utf8" => PayloadBuilder.Snapshot(nameBytes: [0xC0, 0xAF]),
            "name encoded surrogate" => PayloadBuilder.Snapshot(nameBytes: [0xED, 0xA0, 0x80]),
            "name one scalar over max" => PayloadBuilder.Snapshot(nameBytes: Utf8(new string('a', ProtocolLimits.MaxNameScalars + 1))),
            "name one byte over max bytes" => PayloadBuilder.Snapshot(nameBytes: [.. MaxName(), 0x61]),
            "name length claims more than present" => Patch(PayloadBuilder.Snapshot(), NameLengthOffset, [0x00, 0x00, 0x00, 0xC8]),
            "name empty" => PayloadBuilder.Snapshot(nameBytes: []),
            "name with line break" => PayloadBuilder.Snapshot(nameBytes: Utf8("line\r\nbreak")),
            "name with tab" => PayloadBuilder.Snapshot(nameBytes: Utf8("a\tb")),
            "name with DEL" => PayloadBuilder.Snapshot(nameBytes: Utf8("a\u007fb")),
            "name with C1 control" => PayloadBuilder.Snapshot(nameBytes: Utf8("a\u0085b")),
            "name with line separator" => PayloadBuilder.Snapshot(nameBytes: Utf8("a\u2028b")),
            "name with paragraph separator" => PayloadBuilder.Snapshot(nameBytes: Utf8("a\u2029b")),
            "name with byte order mark" => PayloadBuilder.Snapshot(nameBytes: Utf8("\ufeffa")),
            "name with right-to-left override" => PayloadBuilder.Snapshot(nameBytes: Utf8("a\u202eb")),
            "name with right-to-left isolate" => PayloadBuilder.Snapshot(nameBytes: Utf8("a\u2067b")),
            "name with arabic letter mark" => PayloadBuilder.Snapshot(nameBytes: Utf8("a\u061cb")),
            "name with zero width space" => PayloadBuilder.Snapshot(nameBytes: Utf8("a\u200bb")),
            "name with soft hyphen" => PayloadBuilder.Snapshot(nameBytes: Utf8("a\u00adb")),
            "name with interlinear annotation" => PayloadBuilder.Snapshot(nameBytes: Utf8("a\ufff9b")),
            "name with tag character" => PayloadBuilder.Snapshot(nameBytes: Utf8("a\U000E0041")),
            "name of the old unicode vector" => PayloadBuilder.Snapshot(nameBytes: Utf8(VectorBuilder.SnapshotUnicodeNameBeforeD4)),
            "name of the old maximal vector" => PayloadBuilder.Snapshot(nameBytes: Utf8(string.Concat(Enumerable.Repeat("\U0001F600", ProtocolLimits.MaxTextScalars)))),
            "name length huge" => Patch(PayloadBuilder.Snapshot(), NameLengthOffset, [0xFF, 0xFF, 0xFF, 0xFF]),
            "image count 9 declared" => PayloadBuilder.Snapshot(imageCount: 9),
            "image count huge" => PayloadBuilder.Snapshot(imageCount: 0xFFFFFFFF),
            "image count more than present" => PayloadBuilder.Snapshot(imageCount: 3),
            "image count less than present" => PayloadBuilder.Snapshot(imageCount: 1),
            "images unsorted" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset2), PayloadBuilder.Image(Samples.Asset1)]),
            "images duplicate" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1), PayloadBuilder.Image(Samples.Asset1, 0x22)]),
            "image zero asset id" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(default)]),
            "image zero digest" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, digest: new byte[32])]),
            "image format 0" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, format: 0)]),
            "image format 4" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, format: 4)]),
            "image zero bytes" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, byteLength: 0)]),
            "image bytes over max" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, byteLength: (ulong)ProtocolLimits.MaxImageBytes + 1)]),
            "image bytes u64 max" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, byteLength: ulong.MaxValue)]),
            "image zero width" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, width: 0)]),
            "image width over max" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, width: ProtocolLimits.MaxImageDimension + 1)]),
            "image zero height" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, height: 0)]),
            "image height over max" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, height: ProtocolLimits.MaxImageDimension + 1)]),
            "image width u32 max" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, width: uint.MaxValue, height: uint.MaxValue)]),
            "image pixels over max" => PayloadBuilder.Snapshot(images: [PayloadBuilder.Image(Samples.Asset1, width: 5000, height: 4001)]),
            "images total bytes over max" => PayloadBuilder.Snapshot(images: sortedIds.Take(6).Select(id => PayloadBuilder.Image(id, byteLength: (ulong)ProtocolLimits.MaxImageBytes)).ToArray()),
            "trailing byte" => PayloadBuilder.Snapshot(trailing: [0]),
            "truncated image" => PayloadBuilder.Snapshot()[..^1],
            "nested count abuse" => PayloadBuilder.Snapshot(imageCount: 0x7FFFFFFF, images: []),
            _ => throw new ArgumentException(name),
        };
    }

    private static byte[] MaxName() => Utf8(string.Concat(Enumerable.Repeat("\U0001F600", ProtocolLimits.MaxNameScalars)));

    private static byte[] Utf8(string text) => ProtocolConstants.StrictUtf8.GetBytes(text);

    private static byte[] Patch(byte[] payload, int offset, byte[] bytes)
    {
        bytes.CopyTo(payload, offset);
        return payload;
    }
}
