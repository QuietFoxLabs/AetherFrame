using System;
using System.Globalization;
using System.Text;

namespace AetherFrame.Protocol.Encoding;

/// <summary>
/// Text as the protocol carries it: UTF-8 without a byte order mark, no U+0000, and at most
/// <see cref="ProtocolLimits.MaxTextScalars"/> scalar values. Nothing else is changed: no Unicode
/// normalization, no line ending conversion, no trimming, so a text round-trips byte for byte and
/// two texts that differ only in normalization form or line endings are different texts
/// (docs/networking/ProtocolSpecification-v1.md, "Text").
/// </summary>
internal static class ProtocolText
{
    /// <summary>Encodes <paramref name="text"/> for the wire, refusing unpaired surrogates, U+0000 and over-long text.</summary>
    public static byte[] Encode(string text, string field)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Scalars are counted before encoding so an over-long text is refused by its length, not by
        // an encoder exception, and so the byte cap below is the only allocation bound needed.
        var scalars = CountScalars(text, field);
        if (scalars > ProtocolLimits.MaxTextScalars)
        {
            throw TooLong(field, scalars);
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
    public static string Decode(ReadOnlySpan<byte> utf8, string field)
    {
        if (utf8.Length > ProtocolLimits.MaxTextBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"The text field '{field}' is {Number(utf8.Length)} bytes; the limit is {Number(ProtocolLimits.MaxTextBytes)}.");
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
        if (scalars > ProtocolLimits.MaxTextScalars)
        {
            throw TooLong(field, scalars);
        }

        return text;
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

    private static ProtocolException TooLong(string field, int scalars) =>
        new(ProtocolError.LimitExceeded, $"The text field '{field}' holds {Number(scalars)} characters; the limit is {Number(ProtocolLimits.MaxTextScalars)}.");

    private static ProtocolException Unpaired(string field) =>
        new(ProtocolError.InvalidText, $"The text field '{field}' contains an unpaired surrogate.");

    /// <summary>Numbers in messages are always written the same way, whatever the current culture.</summary>
    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
