using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using AetherFrame.Personas.Storage;
using AetherFrame.Protocol.Identity;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The persona registry's bytes (decision P3): exactly the layout the register gives, round trips
/// that keep every record, flag, label and the selection exactly, and a refusal, with its reason,
/// for every rule a registry can break. Every refusal of a rule checked after the checksum is built
/// field by field with a valid checksum, so it is refused for the rule it breaks and not for its
/// checksum; the size and checksum refusals come before it, and each asserts its own reason.
/// </summary>
public class PersonaRegistryCodecTests
{
    private static readonly PersonaPublicKey KeyA = NewKey();
    private static readonly PersonaPublicKey KeyB = NewKey();

    [Fact]
    public void AnEmptyRegistry_IsTheHeaderAndTheTrailer()
    {
        var bytes = PersonaRegistryCodec.Encode(Array.Empty<PersonaRecord>(), default);
        Assert.Equal(4 + 2 + 4 + 16 + 32, bytes.Length);
        Assert.Equal(Registry([]), bytes);

        Assert.True(PersonaRegistryCodec.TryDecode(bytes, out var records, out var active, out var reason));
        Assert.Empty(records);
        Assert.True(active.IsEmpty);
        Assert.Equal("", reason);
    }

    [Fact]
    public void TheLayout_IsExactlyTheRegisters()
    {
        // Fixed slots and the two public keys the P-256 specification gives, so every byte but the
        // checksum is written out here from the layout alone.
        var first = new PersonaRecord(PersonaSlotId.Parse("slot_00112233445566778899aabbccddeeff"), SyntheticKeys.BasePoint, "Main");
        var second = new PersonaRecord(PersonaSlotId.Parse("slot_ffeeddccbbaa99887766554433221100"), SyntheticKeys.NegatedBasePoint, "Alt", acknowledged: true);
        var expected = Convert.FromHexString(
            "41465052" + "0001" + "00000002"
            + "00112233445566778899AABBCCDDEEFF" + Convert.ToHexString(SyntheticKeys.BasePoint.ToArray()) + "00" + "0004" + "004D00610069006E"
            + "FFEEDDCCBBAA99887766554433221100" + Convert.ToHexString(SyntheticKeys.NegatedBasePoint.ToArray()) + "01" + "0003" + "0041006C0074"
            + "FFEEDDCCBBAA99887766554433221100");
        expected = [.. expected, .. SHA256.HashData(expected)];

        Assert.Equal(expected, PersonaRegistryCodec.Encode([first, second], second.Slot));
    }

    [Fact]
    public void Records_RoundTrip_InOrder_WithTheirFlagsLabelsAndTheSelection()
    {
        // A supplementary character, built from its code point, travels as its two code units.
        var fox = char.ConvertFromUtf32(0x1F98A);
        PersonaRecord[] records =
        [
            new(PersonaSlotId.NewId(), NewKey(), "Main"),
            new(PersonaSlotId.NewId(), NewKey(), "Fox " + fox, acknowledged: true),
            new(PersonaSlotId.NewId(), NewKey(), new string('x', PersonaLabel.MaxLength)),
        ];

        foreach (var selected in new[] { default, records[0].Slot, records[2].Slot })
        {
            var bytes = PersonaRegistryCodec.Encode(records, selected);
            Assert.True(PersonaRegistryCodec.TryDecode(bytes, out var decoded, out var active, out var reason), reason);
            Assert.Equal(selected, active);
            AssertSameRecords(records, decoded);
            Assert.Equal(bytes, PersonaRegistryCodec.Encode(decoded, active));
        }
    }

    [Fact]
    public void TheLargestRegistry_IsMaxRegistryBytes_AndDecodes()
    {
        var records = Enumerable.Range(0, PersonaManager.MaxPersonas)
            .Select(i => new PersonaRecord(PersonaSlotId.NewId(), NewKey(), new string((char)('a' + (i % 26)), PersonaLabel.MaxLength), acknowledged: i % 2 == 0))
            .ToArray();

        var bytes = PersonaRegistryCodec.Encode(records, records[^1].Slot);
        Assert.Equal(54_330, PersonaManager.MaxRegistryBytes);
        Assert.Equal(PersonaManager.MaxRegistryBytes, bytes.Length);
        Assert.True(PersonaRegistryCodec.TryDecode(bytes, out var decoded, out var active, out var reason), reason);
        AssertSameRecords(records, decoded);
        Assert.Equal(records[^1].Slot, active);
    }

    [Fact]
    public void Encode_RefusesMoreRecordsThanTheRegistryHolds()
    {
        var records = Enumerable.Range(0, PersonaManager.MaxPersonas + 1).Select(_ => new PersonaRecord(PersonaSlotId.NewId(), KeyA, "Main")).ToArray();
        Assert.Equal(PersonaError.RegistryFull, Assert.Throws<PersonaException>(() => PersonaRegistryCodec.Encode(records, default)).Error);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void TryDecode_RefusesEveryBrokenRule_SaysWhich_AndHandsOutNothing(string rule, byte[] bytes, string because)
    {
        Assert.False(PersonaRegistryCodec.TryDecode(bytes, out var records, out var active, out var reason), rule);
        Assert.Contains(because, reason, StringComparison.Ordinal);
        Assert.Empty(records);
        Assert.True(active.IsEmpty);
    }

    [Fact]
    public void TryDecode_TellsALaterVersionApart_BeforeItsChecksum_AndNothingElse()
    {
        var good = Record(Slot(PersonaSlotId.NewId()), KeyA.ToArray(), 0, "Main");
        var later = Registry([good], version: 2);
        var laterLayout = (byte[])later.Clone();
        laterLayout[^1] ^= 0x01;

        foreach (var bytes in new[] { later, laterLayout, Registry([good], version: ushort.MaxValue) })
        {
            Assert.False(PersonaRegistryCodec.TryDecode(bytes, out var records, out var active, out var reason, out var newerVersion));
            Assert.True(newerVersion, reason);
            Assert.Empty(records);
            Assert.True(active.IsEmpty);
        }

        var damaged = Registry([good]);
        damaged[20] ^= 0x01;
        foreach (var bytes in new[] { Registry([good], version: 0), Registry([good], magic: "AFPS"u8.ToArray()), damaged, Array.Empty<byte>() })
        {
            Assert.False(PersonaRegistryCodec.TryDecode(bytes, out _, out _, out var reason, out var newerVersion));
            Assert.False(newerVersion, reason);
        }

        Assert.True(PersonaRegistryCodec.TryDecode(Registry([good]), out _, out _, out _, out var current));
        Assert.False(current);
    }

    public static IEnumerable<object[]> Refusals()
    {
        var slot = PersonaSlotId.NewId();
        var other = PersonaSlotId.NewId();
        var good = Record(Slot(slot), KeyA.ToArray(), 0, "Main");
        var offCurve = KeyA.ToArray();
        offCurve[^1] ^= 0x01;
        var fox = char.ConvertFromUtf32(0x1F98A);
        var flipped = Registry([good]);
        flipped[20] ^= 0x01;

        yield return Case("larger than the limit", new byte[PersonaManager.MaxRegistryBytes + 1], "larger than");
        yield return Case("shorter than an empty registry", Registry([])[..^1], "shorter than");
        yield return Case("no bytes at all", [], "shorter than");
        yield return Case("a flipped bit", flipped, "checksum");
        yield return Case("another magic", Registry([good], magic: "AFPS"u8.ToArray()), "magic");
        yield return Case("a later version", Registry([good], version: 2), "version");
        yield return Case("version zero", Registry([good], version: 0), "version");
        yield return Case("more personas declared than the limit", Registry([good], count: PersonaManager.MaxPersonas + 1), "more than");
        yield return Case("a count of all ones", Registry([good], count: uint.MaxValue), "more than");
        yield return Case("more records declared than held", Registry([good], count: 2), "record is cut short");
        yield return Case("an empty slot", Registry([Record(new byte[16], KeyA.ToArray(), 0, "Main")]), "empty or repeated");
        yield return Case("a repeated slot", Registry([good, Record(Slot(slot), KeyB.ToArray(), 0, "Alt")]), "empty or repeated");
        yield return Case("a key off the curve", Registry([Record(Slot(slot), offCurve, 0, "Main")]), "P-256");
        yield return Case("one identity twice", Registry([good, Record(Slot(other), KeyA.ToArray(), 0, "Alt")]), "same identity");
        yield return Case("an unknown flag", Registry([Record(Slot(slot), KeyA.ToArray(), 0x02, "Main")]), "flag");
        yield return Case("every flag", Registry([Record(Slot(slot), KeyA.ToArray(), 0xFF, "Main")]), "flag");
        yield return Case("an empty label", Registry([Record(Slot(slot), KeyA.ToArray(), 0, "")]), "out of range");
        yield return Case("a label over the limit", Registry([Record(Slot(slot), KeyA.ToArray(), 0, new string('x', PersonaLabel.MaxLength + 1))]), "out of range");
        yield return Case("a label cut short", Registry([Record(Slot(slot), KeyA.ToArray(), 0, "Main", declaredLength: 5)]), "label is cut short");
        yield return Case("a label with surrounding space", Registry([Record(Slot(slot), KeyA.ToArray(), 0, " Main")]), "label rule");
        yield return Case("a label of whitespace", Registry([Record(Slot(slot), KeyA.ToArray(), 0, "   ")]), "label rule");
        yield return Case("a label with a control character", Registry([Record(Slot(slot), KeyA.ToArray(), 0, "Ma\u0007in")]), "label rule");
        yield return Case("a label with an unpaired surrogate", Registry([Record(Slot(slot), KeyA.ToArray(), 0, "Fox " + fox[0])]), "label rule");
        yield return Case("bytes after the last record", Registry([good], between: [0]), "follow the last record");
        yield return Case("a selection naming no record", Registry([good], active: Slot(other)), "names no record");
        yield return Case("a selection with no records", Registry([], active: Slot(other)), "names no record");
    }

    private static object[] Case(string rule, byte[] bytes, string because) => [rule, bytes, because];

    private static PersonaPublicKey NewKey()
    {
        using var material = PersonaKeyMaterial.Generate();
        return material.PublicKey;
    }

    private static byte[] Slot(PersonaSlotId slot)
    {
        var bytes = new byte[PersonaSlotId.ByteLength];
        slot.WriteBytes(bytes);
        return bytes;
    }

    /// <summary>One record, field by field: slot, public key, flags, label length (declared, or the label's), label as UTF-16BE code units.</summary>
    private static byte[] Record(byte[] slot, byte[] publicKey, byte flags, string label, int? declaredLength = null)
    {
        var bytes = new List<byte>();
        bytes.AddRange(slot);
        bytes.AddRange(publicKey);
        bytes.Add(flags);
        bytes.AddRange(BigEndian((ushort)(declaredLength ?? label.Length)));
        foreach (var c in label)
        {
            bytes.AddRange(BigEndian(c));
        }

        return bytes.ToArray();
    }

    /// <summary>A registry, field by field, with a checksum over everything before it that is always right.</summary>
    private static byte[] Registry(byte[][] records, byte[]? active = null, uint? count = null, byte[]? magic = null, ushort version = 1, byte[]? between = null)
    {
        var bytes = new List<byte>();
        bytes.AddRange(magic ?? "AFPR"u8.ToArray());
        bytes.AddRange(BigEndian(version));
        var declared = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(declared, count ?? (uint)records.Length);
        bytes.AddRange(declared);
        foreach (var record in records)
        {
            bytes.AddRange(record);
        }

        bytes.AddRange(between ?? []);
        bytes.AddRange(active ?? new byte[PersonaSlotId.ByteLength]);
        var body = bytes.ToArray();
        return [.. body, .. SHA256.HashData(body)];
    }

    private static byte[] BigEndian(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static void AssertSameRecords(IReadOnlyList<PersonaRecord> expected, IReadOnlyList<PersonaRecord> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Slot, actual[index].Slot);
            Assert.Equal(expected[index].PublicKey, actual[index].PublicKey);
            Assert.Equal(expected[index].Label, actual[index].Label, StringComparer.Ordinal);
            Assert.Equal(expected[index].Acknowledged, actual[index].Acknowledged);
        }
    }
}
