using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using System.Threading.Tasks;
using AetherFrame.Persistence;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A stored file's bytes decode to exactly the text <see cref="File.ReadAllText(string)"/> and
/// File.ReadAllTextAsync read from the same file, which is what every build since 0.1.6 has read:
/// UTF-8 by default, and UTF-8, UTF-16 or UTF-32 of either byte order behind its byte order mark,
/// which is never part of the text. The decoder also says whether any of those bytes wasn't valid
/// in that encoding: a malformed, overlong, out-of-range or truncated UTF-8 sequence, an encoded or
/// lone surrogate, a partial code unit, or a UTF-32 value outside Unicode isn't; a validly encoded
/// U+FFFD, noncharacter, emoji or NUL is ordinary text. A write's text must come back unchanged
/// through the UTF-8 round trip a file makes, so text holding a lone surrogate (which UTF-8 can't
/// carry) or starting with U+FEFF (which would read back as a byte order mark) is refused.
///
/// <para>Both answers are checked against references that don't depend on how the decoder works,
/// for a seeded random corpus as well as the cases that matter: the runtime's own file readers,
/// reading the same bytes from disk, for the text, and each encoding's rules
/// (<see cref="Utf8.IsValid(ReadOnlySpan{byte})"/> for UTF-8) for validity.</para>
/// </summary>
public class StoredTextDecoderTests
{
    private const int Seed = 20260929;

    private const int CorpusSize = 2000;

    private const int MaxBodyLength = 9000;

    private const string Utf8Name = "UTF-8";

    private const string Utf16LeName = "UTF-16 (little-endian)";

    private const string Utf16BeName = "UTF-16 (big-endian)";

    private const string Utf32LeName = "UTF-32 (little-endian)";

    private const string Utf32BeName = "UTF-32 (big-endian)";

    /// <summary>Well-formed text a strict decoder must still accept: a literal U+FFFD, the
    /// noncharacters U+FFFE and U+FFFF, an emoji (a surrogate pair in UTF-16), NUL, a right-to-left
    /// mark before Hebrew, a combining mark, and a U+FEFF inside the text.</summary>
    private const string WellFormedText = "{ \"Name\": \"\u00C6lfwyn \uFFFD \uFFFE \uFFFF \U0001F338 \0 \u200F\u05D0 e\u0301 \uFEFF!\" }";

    private static readonly string[] EncodingNames = [Utf8Name, Utf16LeName, Utf16BeName, Utf32LeName, Utf32BeName];

    private static readonly (string Name, byte[] Bytes) NoMark = ("no mark", []);

    private static readonly (string Name, byte[] Bytes) Utf8Mark = ("UTF-8 mark", [0xEF, 0xBB, 0xBF]);

    private static readonly (string Name, byte[] Bytes) Utf16LeMark = ("UTF-16 LE mark", [0xFF, 0xFE]);

    private static readonly (string Name, byte[] Bytes) Utf16BeMark = ("UTF-16 BE mark", [0xFE, 0xFF]);

    private static readonly (string Name, byte[] Bytes) Utf32LeMark = ("UTF-32 LE mark", [0xFF, 0xFE, 0x00, 0x00]);

    private static readonly (string Name, byte[] Bytes) Utf32BeMark = ("UTF-32 BE mark", [0x00, 0x00, 0xFE, 0xFF]);

    private static readonly (string Name, byte[] Bytes)[] Marks =
    [
        NoMark,
        Utf8Mark,
        Utf16LeMark,
        Utf16BeMark,
        Utf32LeMark,
        Utf32BeMark,
        ("partial mark EF BB", [0xEF, 0xBB]),
        ("partial mark FF", [0xFF]),
        ("partial mark 00 00 FE", [0x00, 0x00, 0xFE]),
    ];

    private static readonly int[] NotableScalars = [0xFFFD, 0xFEFF, 0xFFFE, 0xFFFF, 0x200F, 0x0301, 0x0000, 0x1F338, 0x10FFFF];

    /// <summary>The sizes a reader's buffer may split a file at.</summary>
    private static readonly int[] Boundaries = [1024, 2048, 4096, 8192];

    private static readonly byte[] Tail = "tail"u8.ToArray();

    /// <summary>Files that matter on their own, by name.</summary>
    private static readonly Dictionary<string, byte[]> EdgeCases = new()
    {
        ["empty"] = [],
        ["UTF-8 mark alone"] = Utf8Mark.Bytes,
        ["UTF-16 LE mark alone"] = Utf16LeMark.Bytes,
        ["UTF-16 BE mark alone"] = Utf16BeMark.Bytes,
        ["UTF-32 LE mark alone"] = Utf32LeMark.Bytes,
        ["UTF-32 BE mark alone"] = Utf32BeMark.Bytes,
        ["partial mark EF BB alone"] = [0xEF, 0xBB],
        ["partial mark EF BB before text"] = [0xEF, 0xBB, .. "{}"u8],
        ["partial mark FF alone"] = [0xFF],
        ["partial mark FF before text"] = [0xFF, .. "{}"u8],
        ["partial mark 00 00 FE alone"] = [0x00, 0x00, 0xFE],
        ["partial mark 00 00 FE before text"] = [0x00, 0x00, 0xFE, .. "{}"u8],
        ["UTF-16 LE mark and one byte, too short for UTF-32"] = [0xFF, 0xFE, 0x00],
        ["UTF-8 mark twice"] = [.. Utf8Mark.Bytes, .. Utf8Mark.Bytes, .. "{}"u8],
        ["UTF-16 LE mark twice"] = [.. Utf16LeMark.Bytes, .. Utf16LeMark.Bytes, .. Encoding.Unicode.GetBytes("{}")],
        ["UTF-8 mark then UTF-16 LE mark"] = [.. Utf8Mark.Bytes, .. Utf16LeMark.Bytes, .. "{}"u8],
        ["UTF-16 LE with an odd trailing byte"] = [.. Utf16LeMark.Bytes, .. Encoding.Unicode.GetBytes("{}"), 0x20],
        ["UTF-16 BE with an odd trailing byte"] = [.. Utf16BeMark.Bytes, .. Encoding.BigEndianUnicode.GetBytes("{}"), 0x20],
        ["UTF-32 LE with a partial last value"] = [.. Utf32LeMark.Bytes, .. Encoding.UTF32.GetBytes("{}"), 0x20, 0x00],
        ["UTF-8 truncated by the end of the file"] = [.. "{ \"Name\": \"x"u8, 0xE2, 0x82],
        ["UTF-8 four-byte sequence truncated by the end of the file"] = [.. "{}"u8, 0xF0, 0x9F, 0x8C],
        ["UTF-16 without a mark"] = Encoding.Unicode.GetBytes("{}"),
        ["well-formed UTF-8 without a mark"] = Encode("UTF-8 without a mark", WellFormedText),
        ["well-formed UTF-8"] = Encode("UTF-8", WellFormedText),
        ["well-formed UTF-16 LE"] = Encode("UTF-16 LE", WellFormedText),
        ["well-formed UTF-16 BE"] = Encode("UTF-16 BE", WellFormedText),
        ["well-formed UTF-32 LE"] = Encode("UTF-32 LE", WellFormedText),
        ["well-formed UTF-32 BE"] = Encode("UTF-32 BE", WellFormedText),
    };

    public static TheoryData<string> EdgeCaseNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var name in EdgeCases.Keys)
            {
                names.Add(name);
            }

            return names;
        }
    }

    /// <summary>One file checked against the references: the encoding its mark selects, whether its
    /// bytes are valid in that encoding, and every way the decoder disagreed.</summary>
    private sealed record CheckedFile(string EncodingName, bool IsValid, int Length, List<string> Disagreements);

    private static Encoding MarkedEncoding(string encoding) => encoding switch
    {
        "UTF-8 without a mark" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        "UTF-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        "UTF-16 LE" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
        "UTF-16 BE" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        "UTF-32 LE" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
        "UTF-32 BE" => new UTF32Encoding(bigEndian: true, byteOrderMark: true),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null),
    };

    /// <summary><paramref name="text"/> as a file in <paramref name="encoding"/>, behind its byte order mark if it has one.</summary>
    private static byte[] Encode(string encoding, string text)
    {
        var chosen = MarkedEncoding(encoding);
        return [.. chosen.GetPreamble(), .. chosen.GetBytes(text)];
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    private static string ReadAsFile(byte[] bytes)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "stored.json");
        File.WriteAllBytes(path, bytes);
        return File.ReadAllText(path);
    }

    /// <summary>The encoding a byte order mark at the start of <paramref name="bytes"/> selects
    /// (UTF-8 when there is none), and the mark's length.</summary>
    private static (string Name, int MarkLength) Detect(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xEF, 0xBB, 0xBF, ..] => (Utf8Name, 3),
        [0xFF, 0xFE, 0x00, 0x00, ..] => (Utf32LeName, 4),
        [0xFF, 0xFE, ..] => (Utf16LeName, 2),
        [0xFE, 0xFF, ..] => (Utf16BeName, 2),
        [0x00, 0x00, 0xFE, 0xFF, ..] => (Utf32BeName, 4),
        _ => (Utf8Name, 0),
    };

    /// <summary>Whether <paramref name="body"/> is valid in <paramref name="encoding"/>, by that
    /// encoding's own rules: <see cref="Utf8.IsValid(ReadOnlySpan{byte})"/> for UTF-8, whole code
    /// units with every surrogate paired for UTF-16, whole values that are all Unicode scalar values
    /// for UTF-32.</summary>
    private static bool IsValidIn(string encoding, ReadOnlySpan<byte> body) => encoding switch
    {
        Utf8Name => Utf8.IsValid(body),
        Utf16LeName => IsValidUtf16(body, bigEndian: false),
        Utf16BeName => IsValidUtf16(body, bigEndian: true),
        Utf32LeName => IsValidUtf32(body, bigEndian: false),
        _ => IsValidUtf32(body, bigEndian: true),
    };

    private static bool IsValidUtf16(ReadOnlySpan<byte> body, bool bigEndian)
    {
        if (body.Length % 2 != 0)
        {
            return false;
        }

        var units = new char[body.Length / 2];
        for (var i = 0; i < units.Length; i++)
        {
            var unit = body.Slice(2 * i, 2);
            units[i] = (char)(bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(unit) : BinaryPrimitives.ReadUInt16LittleEndian(unit));
        }

        return IsWellFormed(units);
    }

    private static bool IsValidUtf32(ReadOnlySpan<byte> body, bool bigEndian)
    {
        if (body.Length % 4 != 0)
        {
            return false;
        }

        for (var i = 0; i < body.Length; i += 4)
        {
            var value = body.Slice(i, 4);
            if (!Rune.IsValid(bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(value) : BinaryPrimitives.ReadUInt32LittleEndian(value)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when every surrogate in <paramref name="text"/> is half of a pair.</summary>
    private static bool IsWellFormed(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decodes every case and compares it with the references: the text File.ReadAllText and
    /// File.ReadAllTextAsync read from the same bytes on disk, the encoding its byte order mark
    /// selects, and whether its bytes are valid in that encoding.
    /// </summary>
    private static async Task<List<CheckedFile>> CheckAgainstReferencesAsync(IEnumerable<(string Name, byte[] Bytes)> cases)
    {
        using var directory = new TempDirectory();
        var results = new List<CheckedFile>();
        var index = 0;

        foreach (var (name, bytes) in cases)
        {
            // A file of its own for each case: on Windows a scanner may still hold the previous one.
            var path = Path.Combine(directory.Path, $"stored-{index++}.json");
            await File.WriteAllBytesAsync(path, bytes);
            var decoded = StoredTextDecoder.Decode(bytes);
            var (encoding, markLength) = Detect(bytes);
            var valid = IsValidIn(encoding, bytes.AsSpan(markLength));
            var disagreements = new List<string>();

            if (!string.Equals(decoded.Text, File.ReadAllText(path), StringComparison.Ordinal))
            {
                disagreements.Add($"{name}: the text isn't what File.ReadAllText reads");
            }

            if (!string.Equals(decoded.Text, await File.ReadAllTextAsync(path), StringComparison.Ordinal))
            {
                disagreements.Add($"{name}: the text isn't what File.ReadAllTextAsync reads");
            }

            if (!string.Equals(decoded.EncodingName, encoding, StringComparison.Ordinal))
            {
                disagreements.Add($"{name}: read as {decoded.EncodingName}, but its bytes select {encoding}");
            }

            if (decoded.HasInvalidBytes == valid)
            {
                disagreements.Add($"{name}: HasInvalidBytes is {decoded.HasInvalidBytes} for {(valid ? "valid" : "invalid")} {encoding}");
            }

            results.Add(new CheckedFile(encoding, valid, bytes.Length, disagreements));
        }

        return results;
    }

    private static int RandomScalar(Random random) => random.Next(10) switch
    {
        < 4 => random.Next(0x20, 0x7F),
        4 => random.Next(0x00, 0x20),
        5 => random.Next(0x80, 0x800),
        6 => random.Next(0x800, 0xD800),
        7 => random.Next(0xE000, 0x10000),
        8 => random.Next(0x10000, 0x110000),
        _ => NotableScalars[random.Next(NotableScalars.Length)],
    };

    private static byte[] RandomBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static byte[] RandomAscii(Random random, int length) =>
        Enumerable.Range(0, length).Select(_ => (byte)random.Next(0x80)).ToArray();

    private static byte[] RandomUtf8(Random random, int length)
    {
        var bytes = new List<byte>(length + 4);
        Span<byte> encoded = stackalloc byte[4];
        while (bytes.Count < length)
        {
            bytes.AddRange(encoded[..new Rune(RandomScalar(random)).EncodeToUtf8(encoded)]);
        }

        return bytes.ToArray();
    }

    /// <summary>A few of <paramref name="bytes"/> overwritten with arbitrary values.</summary>
    private static byte[] Overwrite(Random random, byte[] bytes)
    {
        for (var i = random.Next(1, 4); i > 0 && bytes.Length > 0; i--)
        {
            bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
        }

        return bytes;
    }

    private static byte[] Utf16Bytes(IEnumerable<char> units, bool bigEndian)
    {
        var bytes = new List<byte>();
        foreach (var unit in units)
        {
            bytes.Add(bigEndian ? (byte)(unit >> 8) : (byte)unit);
            bytes.Add(bigEndian ? (byte)unit : (byte)(unit >> 8));
        }

        return bytes.ToArray();
    }

    private static byte[] Utf32Bytes(IEnumerable<uint> values, bool bigEndian)
    {
        var bytes = new List<byte>();
        var value = new byte[4];
        foreach (var scalar in values)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(value, scalar);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, scalar);
            }

            bytes.AddRange(value);
        }

        return bytes.ToArray();
    }

    /// <summary>UTF-16 of either byte order, which may hold lone surrogates and an odd trailing byte.</summary>
    private static (string Kind, byte[] Bytes, (string Name, byte[] Bytes) NaturalMark) RandomUtf16(Random random, int length)
    {
        var bigEndian = random.Next(2) == 0;
        var loneSurrogates = random.Next(2) == 0;
        var units = new List<char>();
        Span<char> encoded = stackalloc char[2];
        while (2 * units.Count < length)
        {
            if (loneSurrogates && random.Next(50) == 0)
            {
                units.Add((char)random.Next(0xD800, 0xE000));
            }
            else
            {
                units.AddRange(encoded[..new Rune(RandomScalar(random)).EncodeToUtf16(encoded)]);
            }
        }

        var bytes = Utf16Bytes(units, bigEndian);
        if (random.Next(4) == 0)
        {
            bytes = [.. bytes, (byte)random.Next(256)];
        }

        return (bigEndian ? "UTF-16 BE" : "UTF-16 LE", bytes, bigEndian ? Utf16BeMark : Utf16LeMark);
    }

    /// <summary>UTF-32 of either byte order, which may hold surrogates, values above U+10FFFF and a partial last value.</summary>
    private static (string Kind, byte[] Bytes, (string Name, byte[] Bytes) NaturalMark) RandomUtf32(Random random, int length)
    {
        var bigEndian = random.Next(2) == 0;
        var invalidValues = random.Next(2) == 0;
        var values = new List<uint>();
        while (4 * values.Count < length)
        {
            values.Add(!invalidValues || random.Next(50) != 0
                ? (uint)RandomScalar(random)
                : random.Next(2) == 0 ? (uint)random.Next(0xD800, 0xE000) : (uint)random.Next(0x110000, int.MaxValue));
        }

        var bytes = Utf32Bytes(values, bigEndian);
        if (random.Next(4) == 0)
        {
            bytes = [.. bytes, .. RandomBytes(random, random.Next(1, 4))];
        }

        return (bigEndian ? "UTF-32 BE" : "UTF-32 LE", bytes, bigEndian ? Utf32BeMark : Utf32LeMark);
    }

    /// <summary>
    /// A random file body of about <paramref name="length"/> bytes, of one of several kinds so that
    /// every encoding sees valid and invalid input, with the byte order mark it would naturally follow.
    /// </summary>
    private static (string Kind, byte[] Bytes, (string Name, byte[] Bytes) NaturalMark) RandomBody(Random random, int length) => random.Next(6) switch
    {
        0 => ("random bytes", RandomBytes(random, length), NoMark),
        1 => ("ASCII", RandomAscii(random, length), NoMark),
        2 => ("UTF-8", RandomUtf8(random, length), random.Next(2) == 0 ? NoMark : Utf8Mark),
        3 => ("UTF-8 with overwritten bytes", Overwrite(random, RandomUtf8(random, length)), NoMark),
        4 => RandomUtf16(random, length),
        _ => RandomUtf32(random, length),
    };

    /// <summary>
    /// UTF-8 sequences, valid and not, starting at every offset from wholly before to wholly after
    /// each boundary a reader's buffer may split a file at (with and without a UTF-8 byte order mark
    /// shifting them), and UTF-16 surrogates and UTF-32 values around the same boundaries, followed
    /// by more text or ending the file with a partial code unit.
    /// </summary>
    private static IEnumerable<(string Name, byte[] Bytes)> BoundaryCases()
    {
        (string Name, byte[] Bytes)[] utf8 =
        [
            ("80", [0x80]),
            ("C0 AF", [0xC0, 0xAF]),
            ("E0 80 80", [0xE0, 0x80, 0x80]),
            ("ED A0 80", [0xED, 0xA0, 0x80]),
            ("F4 90 80 80", [0xF4, 0x90, 0x80, 0x80]),
            ("F5", [0xF5]),
            ("FF", [0xFF]),
            ("truncated E2 82", [0xE2, 0x82]),
            ("truncated F0 9F 98", [0xF0, 0x9F, 0x98]),
            ("valid E2 82 AC", [0xE2, 0x82, 0xAC]),
            ("valid F0 9F 8C B8", [0xF0, 0x9F, 0x8C, 0xB8]),
            ("valid EF BF BD", [0xEF, 0xBF, 0xBD]),
        ];

        (string Name, char[] Units)[] utf16 =
        [
            ("surrogate pair", ['\uD83C', '\uDF38']),
            ("lone high surrogate", ['\uD83C', 'x']),
            ("lone low surrogate", ['\uDF38']),
            ("U+FFFD", ['\uFFFD']),
        ];

        (string Name, uint[] Values)[] utf32 =
        [
            ("emoji", [0x1F338]),
            ("value above U+10FFFF", [0x110000]),
            ("surrogate value", [0xD800]),
        ];

        foreach (var boundary in Boundaries)
        {
            foreach (var mark in new[] { NoMark, Utf8Mark })
            {
                foreach (var (name, sequence) in utf8)
                {
                    for (var start = boundary - sequence.Length; start <= boundary; start++)
                    {
                        yield return ($"UTF-8 {name} at {start} ({mark.Name})",
                            [.. mark.Bytes, .. Enumerable.Repeat((byte)'a', start - mark.Bytes.Length), .. sequence, .. Tail]);
                    }
                }
            }

            foreach (var bigEndian in new[] { false, true })
            {
                var mark = bigEndian ? Utf16BeMark : Utf16LeMark;
                foreach (var (name, units) in utf16)
                {
                    for (var start = boundary - (2 * units.Length); start <= boundary; start += 2)
                    {
                        char[] text = [.. Enumerable.Repeat('a', (start - mark.Bytes.Length) / 2), .. units, 't', 'a', 'i', 'l'];
                        yield return ($"{mark.Name}, {name} at {start}", [.. mark.Bytes, .. Utf16Bytes(text, bigEndian)]);
                    }
                }

                yield return ($"{mark.Name}, odd trailing byte at {boundary}",
                    [.. mark.Bytes, .. Utf16Bytes(Enumerable.Repeat('a', (boundary - mark.Bytes.Length) / 2), bigEndian), 0x61]);

                var mark32 = bigEndian ? Utf32BeMark : Utf32LeMark;
                foreach (var (name, values) in utf32)
                {
                    foreach (var start in new[] { boundary - 4, boundary })
                    {
                        uint[] text = [.. Enumerable.Repeat(0x61u, (start - mark32.Bytes.Length) / 4), .. values, 0x61u];
                        yield return ($"{mark32.Name}, {name} at {start}", [.. mark32.Bytes, .. Utf32Bytes(text, bigEndian)]);
                    }
                }

                yield return ($"{mark32.Name}, partial last value at {boundary}",
                    [.. mark32.Bytes, .. Utf32Bytes(Enumerable.Repeat(0x61u, (boundary - mark32.Bytes.Length) / 4), bigEndian), 0x61, 0x00]);
            }
        }
    }

    private static string RandomText(Random random, int length, bool loneSurrogates)
    {
        var text = new StringBuilder();
        while (text.Length < length)
        {
            if (loneSurrogates && random.Next(30) == 0)
            {
                text.Append((char)random.Next(0xD800, 0xE000));
            }
            else
            {
                text.Append(new Rune(RandomScalar(random)).ToString());
            }
        }

        return text.ToString();
    }

    /// <summary>What <see cref="VersionedJson.RequireFaithfulReadBack"/> lets a write keep, or null when it refuses.</summary>
    private static string? WriteCheck(string text)
    {
        try
        {
            return VersionedJson.RequireFaithfulReadBack(text, "Test");
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    [Fact]
    public async Task RandomFiles_ReadAsFileReadAllTextReadsThem_AndAreInvalidExactlyWhenTheirBytesAre()
    {
        var random = new Random(Seed);
        var cases = new List<(string Name, byte[] Bytes)>(CorpusSize);
        for (var i = 0; i < CorpusSize; i++)
        {
            var (kind, body, naturalMark) = RandomBody(random, random.Next(MaxBodyLength + 1));
            var mark = random.Next(2) == 0 ? naturalMark : Marks[random.Next(Marks.Length)];
            cases.Add(($"random file {i} ({mark.Name}, {kind}, {mark.Bytes.Length + body.Length} bytes)", [.. mark.Bytes, .. body]));
        }

        var results = await CheckAgainstReferencesAsync(cases);

        Assert.Empty(results.SelectMany(r => r.Disagreements));

        // The corpus reaches every encoding, valid and not, and files past twice the 4096-byte buffer.
        foreach (var encoding in EncodingNames)
        {
            Assert.Contains(results, r => r.EncodingName == encoding && r.IsValid);
            Assert.Contains(results, r => r.EncodingName == encoding && !r.IsValid);
        }

        Assert.Contains(results, r => r.Length > 2 * 4096);
    }

    [Fact]
    public async Task SequencesAcrossTheReadersBufferBoundaries_ReadAsFileReadAllTextReadsThem()
    {
        var results = await CheckAgainstReferencesAsync(BoundaryCases());

        Assert.Empty(results.SelectMany(r => r.Disagreements));
        Assert.Contains(results, r => r.IsValid);
        Assert.Contains(results, r => !r.IsValid);
    }

    [Theory]
    [MemberData(nameof(EdgeCaseNames))]
    public async Task EdgeCaseFile_ReadsAsFileReadAllTextReadsIt(string name)
    {
        var results = await CheckAgainstReferencesAsync([(name, EdgeCases[name])]);

        Assert.Empty(Assert.Single(results).Disagreements);
    }

    [Theory]
    [InlineData("UTF-8 without a mark", Utf8Name)]
    [InlineData("UTF-8", Utf8Name)]
    [InlineData("UTF-16 LE", Utf16LeName)]
    [InlineData("UTF-16 BE", Utf16BeName)]
    [InlineData("UTF-32 LE", Utf32LeName)]
    [InlineData("UTF-32 BE", Utf32BeName)]
    public void WellFormedText_InEveryEncoding_ReadsWithoutItsMark_AndHasNoInvalidBytes(string encoding, string expectedName)
    {
        var decoded = StoredTextDecoder.Decode(Encode(encoding, WellFormedText));

        Assert.Equal(WellFormedText, decoded.Text);
        Assert.False(decoded.HasInvalidBytes);
        Assert.Equal(expectedName, decoded.EncodingName);
    }

    [Theory]
    [InlineData("EF BF BD")]
    [InlineData("EF BB BF EF BF BD")]
    [InlineData("FF FE FD FF")]
    [InlineData("FE FF FF FD")]
    [InlineData("FF FE 00 00 FD FF 00 00")]
    [InlineData("00 00 FE FF 00 00 FF FD")]
    public void ValidlyEncodedReplacementCharacter_IsOrdinaryText(string hex)
    {
        var decoded = StoredTextDecoder.Decode(Hex(hex));

        Assert.Equal("\uFFFD", decoded.Text);
        Assert.False(decoded.HasInvalidBytes);
    }

    [Theory]
    // UTF-8: a lone continuation byte, overlong forms, an encoded surrogate, bytes that never lead a
    // sequence, a value above U+10FFFF, and sequences cut short by the end of the file.
    [InlineData("41 80 42", Utf8Name)]
    [InlineData("41 C0 AF 42", Utf8Name)]
    [InlineData("41 E0 80 80 42", Utf8Name)]
    [InlineData("41 ED A0 80 42", Utf8Name)]
    [InlineData("41 F5 42", Utf8Name)]
    [InlineData("41 FF 42", Utf8Name)]
    [InlineData("41 F4 90 80 80 42", Utf8Name)]
    [InlineData("41 E2 82", Utf8Name)]
    [InlineData("41 F0 9F 8C", Utf8Name)]
    [InlineData("EF BB BF 41 E2 82", Utf8Name)]
    // UTF-16: lone high and low surrogates, a reversed pair, a high surrogate ending the file, an odd trailing byte.
    [InlineData("FF FE 41 00 3C D8 42 00", Utf16LeName)]
    [InlineData("FF FE 41 00 38 DF 42 00", Utf16LeName)]
    [InlineData("FF FE 41 00 38 DF 3C D8", Utf16LeName)]
    [InlineData("FF FE 41 00 3C D8", Utf16LeName)]
    [InlineData("FF FE 41 00 42", Utf16LeName)]
    [InlineData("FE FF 00 41 D8 3C 00 42", Utf16BeName)]
    [InlineData("FE FF 00 41 00", Utf16BeName)]
    // UTF-32: a value above U+10FFFF, surrogate values, a partial last value.
    [InlineData("FF FE 00 00 41 00 00 00 00 00 11 00", Utf32LeName)]
    [InlineData("FF FE 00 00 41 00 00 00 00 D8 00 00", Utf32LeName)]
    [InlineData("FF FE 00 00 41 00 00 00 42 00", Utf32LeName)]
    [InlineData("00 00 FE FF 00 00 00 41 00 11 00 00", Utf32BeName)]
    [InlineData("00 00 FE FF 00 00 00 41 00 00 DF 38", Utf32BeName)]
    public void InvalidBytes_AreReported_AndReadAsFileReadAllTextReadsThem(string hex, string encoding)
    {
        var bytes = Hex(hex);

        var decoded = StoredTextDecoder.Decode(bytes);

        Assert.True(decoded.HasInvalidBytes);
        Assert.Equal(encoding, decoded.EncodingName);
        Assert.Equal(ReadAsFile(bytes), decoded.Text);
        Assert.StartsWith("A", decoded.Text, StringComparison.Ordinal);
        Assert.Contains("\uFFFD", decoded.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", Utf8Name)]
    [InlineData("7B 7D", Utf8Name)]
    [InlineData("7B C3 86 7D", Utf8Name)]
    [InlineData("EF BB BF 7B 7D", Utf8Name)]
    [InlineData("EF BB 7B 7D", Utf8Name)]
    [InlineData("00 00 FE 7B", Utf8Name)]
    [InlineData("7B 00 7D 00", Utf8Name)]
    [InlineData("FF FE 7B 00", Utf16LeName)]
    [InlineData("FF FE 00", Utf16LeName)]
    [InlineData("FE FF 00 7B", Utf16BeName)]
    [InlineData("FF FE 00 00 7B 00 00 00", Utf32LeName)]
    [InlineData("00 00 FE FF 00 00 00 7B", Utf32BeName)]
    public void EncodingName_IsTheEncodingTheByteOrderMarkSelects(string hex, string expected)
    {
        Assert.Equal(expected, StoredTextDecoder.Decode(Hex(hex)).EncodingName);
    }

    [Fact]
    public void WellFormedText_ReadsBackAsItself_AndPassesTheWriteCheck()
    {
        foreach (var json in new[] { string.Empty, "{ \"Name\": \"Plain\" }", WellFormedText })
        {
            var readBack = StoredTextDecoder.ReadBack(json);

            Assert.Equal(json, readBack.Text);
            Assert.False(readBack.HasInvalidBytes);
            Assert.Equal(json, VersionedJson.RequireFaithfulReadBack(json, "Test"));
        }
    }

    [Fact]
    public void TextHoldingALoneSurrogate_DoesNotReadBackAsItself_AndIsRefusedByTheWriteCheck()
    {
        foreach (var json in new[] { "{ \"Name\": \"\uD83C\" }", "{ \"Name\": \"\uDF38\" }", "{ \"Name\": \"\uDF38\uD83C\" }", "{ \"Name\": \"x\" }\uD83C" })
        {
            Assert.NotEqual(json, StoredTextDecoder.ReadBack(json).Text);
            Assert.Throws<InvalidDataException>(() => VersionedJson.RequireFaithfulReadBack(json, "Test"));
        }
    }

    [Fact]
    public void TextStartingWithAByteOrderMarkCharacter_WouldReadBackWithoutIt_AndIsRefusedByTheWriteCheck()
    {
        const string json = "\uFEFF{ \"Name\": \"Plain\" }";

        Assert.Equal(json[1..], StoredTextDecoder.ReadBack(json).Text);
        Assert.Throws<InvalidDataException>(() => VersionedJson.RequireFaithfulReadBack(json, "Test"));
    }

    [Fact]
    public void RandomText_PassesTheWriteCheckExactlyWhenItWouldReadBackAsItself()
    {
        var random = new Random(Seed);
        var disagreements = new List<string>();
        var seen = new HashSet<bool>();
        for (var i = 0; i < 1000; i++)
        {
            var text = RandomText(random, random.Next(200), loneSurrogates: random.Next(2) == 0);

            // UTF-8 can't carry a lone surrogate, and a leading U+FEFF reads back as a byte order mark.
            var faithful = IsWellFormed(text) && !text.StartsWith('\uFEFF');
            seen.Add(faithful);

            var kept = WriteCheck(text);
            if (faithful ? !string.Equals(kept, text, StringComparison.Ordinal) : kept is not null)
            {
                disagreements.Add($"text {i} ({(faithful ? "faithful" : "not faithful")}) was {(kept is null ? "refused" : "kept")}");
            }
        }

        Assert.Empty(disagreements);
        Assert.Equal([false, true], seen.Order());
    }
}
