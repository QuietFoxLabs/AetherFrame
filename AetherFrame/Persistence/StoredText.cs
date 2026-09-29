using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace AetherFrame.Persistence;

/// <summary>
/// One copy of a stored file as the Libraries read it: its text, decoded exactly as every
/// AetherFrame since 0.1.6 has decoded it (see <see cref="StoredTextDecoder"/>), and whether
/// every one of its bytes was valid in that encoding.
///
/// <para>Invalid bytes still read, as U+FFFD, exactly as before: a file is never refused, read
/// from an older backup, or rebuilt because of its encoding alone. <see cref="HasInvalidBytes"/>
/// only tells the Libraries that the text in memory is not a faithful copy of the file, so the
/// file's original bytes must be kept in Recovery before anything writes over it. A U+FFFD that the
/// file encodes validly (EF BF BD in UTF-8) is ordinary text and never sets it.</para>
/// </summary>
/// <param name="Text">The decoded text; a byte order mark is honoured, never kept as text.</param>
/// <param name="HasInvalidBytes">True when a byte sequence wasn't valid in <paramref name="EncodingName"/>.</param>
/// <param name="EncodingName">The encoding the text was decoded with, for logs.</param>
internal readonly record struct StoredText(string Text, bool HasInvalidBytes, string EncodingName);

/// <summary>
/// Decodes a stored file's bytes the way <see cref="File.ReadAllText(string)"/> does, and checks
/// them at the byte level.
///
/// <para>The text is produced by the same <see cref="StreamReader"/> configuration
/// <c>File.ReadAllText</c> uses (UTF-8 by default, byte order marks detected), so it is identical
/// to what 0.1.6 read, for every input: a UTF-8 file with or without a byte order mark, and a UTF-16
/// (little- or big-endian) or UTF-32 (little- or big-endian) file that starts with its byte order
/// mark. A UTF-16 or UTF-32 file without one reads as UTF-8, as it always did (and, holding NUL
/// characters, is not valid JSON). The validity check then decodes the same bytes again with a
/// strict decoder of the detected encoding, which throws on any invalid sequence: a lone or
/// overlong UTF-8 sequence, one truncated by the end of the file, an encoded surrogate, a lone
/// UTF-16 surrogate, an odd trailing byte, or a UTF-32 value outside Unicode.</para>
///
/// <para>AetherFrame itself only ever writes UTF-8 without a byte order mark (Dalamud's storage
/// encodes with <see cref="System.Text.Encoding.UTF8"/>, and so does <see cref="SystemFileStore"/>),
/// and its serializer escapes everything outside ASCII, so what it writes is always valid.</para>
/// </summary>
internal static class StoredTextDecoder
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16LittleEndian = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16BigEndian = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf32LittleEndian = new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true);
    private static readonly Encoding StrictUtf32BigEndian = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true);

    /// <summary>Reads and decodes a file on disk. I/O failures are thrown as they are.</summary>
    internal static async Task<StoredText> ReadFileAsync(string path) =>
        Decode(await File.ReadAllBytesAsync(path).ConfigureAwait(false));

    internal static StoredText Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        // What AetherFrame writes: ASCII, no byte order mark. Valid by definition, and decoded as
        // ASCII by every path below.
        if (Ascii.IsValid(bytes))
        {
            return new StoredText(Encoding.ASCII.GetString(bytes), HasInvalidBytes: false, "UTF-8");
        }

        // Exactly File.ReadAllText(path): new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true).
        string text;
        Encoding detected;
        using (var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
            detected = reader.CurrentEncoding;
        }

        var (strict, name) = detected.CodePage switch
        {
            1200 => (StrictUtf16LittleEndian, "UTF-16 (little-endian)"),
            1201 => (StrictUtf16BigEndian, "UTF-16 (big-endian)"),
            12000 => (StrictUtf32LittleEndian, "UTF-32 (little-endian)"),
            12001 => (StrictUtf32BigEndian, "UTF-32 (big-endian)"),
            _ => (StrictUtf8, "UTF-8"),
        };

        // The reader skipped the detected encoding's byte order mark, if the file starts with one.
        var preamble = detected.Preamble;
        var body = bytes.AsSpan().StartsWith(preamble) ? bytes.AsSpan(preamble.Length) : bytes.AsSpan();

        bool valid;
        try
        {
            // Equal text as well as a strict decode: anything the reader produced differently
            // (which no valid input can cause) is conservatively treated as not faithful.
            valid = string.Equals(strict.GetString(body), text, StringComparison.Ordinal);
        }
        catch (DecoderFallbackException)
        {
            valid = false;
        }

        return new StoredText(text, HasInvalidBytes: !valid, name);
    }

    /// <summary>
    /// The copy a write of <paramref name="contents"/> would read back as: written as UTF-8 (what
    /// both stores write), then decoded here. Equal to <paramref name="contents"/> except for text
    /// holding a lone surrogate, which UTF-8 can't encode and the encoder silently replaces, or
    /// starting with U+FEFF, which reads back as a byte order mark and is dropped.
    /// </summary>
    internal static StoredText ReadBack(string contents) => Decode(Encoding.UTF8.GetBytes(contents));
}
