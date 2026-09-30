using System;
using System.Globalization;
using System.Text;

namespace AetherFrame.Protocol.Encoding;

/// <summary>
/// Text as the protocol carries it: UTF-8 without a byte order mark, no U+0000, and at most
/// <see cref="ProtocolLimits.MaxTextScalars"/> scalar values, or a field's own smaller limit.
/// Nothing else is changed: no Unicode normalization, no line ending conversion, no trimming, so a
/// text round-trips byte for byte and two texts that differ only in normalization form or line
/// endings are different texts (docs/networking/ProtocolSpecification-v1.md, "Text").
/// </summary>
internal static class ProtocolText
{
    /// <summary>Encodes <paramref name="text"/> for the wire, refusing unpaired surrogates, U+0000 and over-long text.</summary>
    public static byte[] Encode(string text, string field) => Encode(text, field, ProtocolLimits.MaxTextScalars);

    /// <summary>Encodes <paramref name="text"/> for a field whose limit is <paramref name="maxScalars"/> scalars.</summary>
    public static byte[] Encode(string text, string field, int maxScalars)
    {
        ArgumentNullException.ThrowIfNull(text);
        CheckLimit(maxScalars);

        // Scalars are counted before encoding so an over-long text is refused by its length, not by
        // an encoder exception, and so the byte cap below is the only allocation bound needed.
        var scalars = CountScalars(text, field);
        if (scalars > maxScalars)
        {
            throw TooLong(field, scalars, maxScalars);
        }

        try
        {
            return ProtocolConstants.StrictUtf8.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            throw Unpaired(field);
        }
    }

    /// <summary>Decodes wire bytes, refusing malformed UTF-8, U+0000 and over-long text.</summary>
    public static string Decode(ReadOnlySpan<byte> utf8, string field) => Decode(utf8, field, ProtocolLimits.MaxTextScalars);

    /// <summary>
    /// Decodes wire bytes for a field whose limit is <paramref name="maxScalars"/> scalars, checking
    /// in reading order: the byte limit (four bytes a scalar), UTF-8, U+0000, then the scalar limit.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> utf8, string field, int maxScalars)
    {
        CheckLimit(maxScalars);
        var maxBytes = maxScalars * 4;
        if (utf8.Length > maxBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"The text field '{field}' is {Number(utf8.Length)} bytes; the limit is {Number(maxBytes)}.");
        }

        string text;
        try
        {
            text = ProtocolConstants.StrictUtf8.GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            throw new ProtocolException(ProtocolError.InvalidText, $"The text field '{field}' is not valid UTF-8.");
        }

        var scalars = CountScalars(text, field);
        if (scalars > maxScalars)
        {
            throw TooLong(field, scalars, maxScalars);
        }

        return text;
    }

    /// <summary>The scalar values in <paramref name="text"/>, which is already known to hold no unpaired surrogate.</summary>
    internal static int ScalarCount(string text)
    {
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                index++;
            }

            count++;
        }

        return count;
    }

    /// <summary>Counts scalar values, refusing U+0000 and unpaired surrogates on the way.</summary>
    private static int CountScalars(string text, string field)
    {
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (c == '\0')
            {
                throw new ProtocolException(ProtocolError.InvalidText, $"The text field '{field}' contains U+0000.");
            }

            if (char.IsHighSurrogate(c))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    throw Unpaired(field);
                }

                index++;
            }
            else if (char.IsLowSurrogate(c))
            {
                throw Unpaired(field);
            }

            count++;
        }

        return count;
    }

    private static void CheckLimit(int maxScalars)
    {
        if (maxScalars < 1 || maxScalars > ProtocolLimits.MaxTextScalars)
        {
            throw new ArgumentOutOfRangeException(nameof(maxScalars), "A field's text limit is 1 to the protocol's text limit.");
        }
    }

    private static ProtocolException TooLong(string field, int scalars, int maxScalars) =>
        new(ProtocolError.LimitExceeded, $"The text field '{field}' holds {Number(scalars)} characters; the limit is {Number(maxScalars)}.");

    private static ProtocolException Unpaired(string field) =>
        new(ProtocolError.InvalidText, $"The text field '{field}' contains an unpaired surrogate.");

    /// <summary>Numbers in messages are always written the same way, whatever the current culture.</summary>
    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
