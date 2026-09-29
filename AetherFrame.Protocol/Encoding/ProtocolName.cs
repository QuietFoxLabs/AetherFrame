using System;
using System.Globalization;
using System.Text;

namespace AetherFrame.Protocol.Encoding;

/// <summary>
/// A Plate's remote name (decision D4, docs/networking/DecisionRegister.md): 1 to
/// <see cref="ProtocolLimits.MaxNameScalars"/> Unicode scalar values in at most
/// <see cref="ProtocolLimits.MaxNameBytes"/> bytes of UTF-8, with none of the refused code points
/// below, and nothing normalized. The refused list is fixed code points, not Unicode properties, so
/// what is valid never changes with the Unicode version.
/// <para>
/// A reader checks in this order and stops at the first fault: an empty name (as soon as its
/// length is read), the byte limit, UTF-8 validity, a refused code point (which includes U+0000),
/// then the scalar limit (docs/networking/ProtocolSpecification-v1.md, "Input with several faults").
/// </para>
/// </summary>
internal static class ProtocolName
{
    /// <summary>Encodes <paramref name="name"/> for the wire, refusing anything a reader would refuse.</summary>
    public static byte[] Encode(string name, string field)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            throw Empty(field);
        }

        // UTF-8 never takes fewer bytes than UTF-16 code units, so a longer string is over the byte
        // limit whatever it holds, and is refused before anything is encoded.
        if (name.Length > ProtocolLimits.MaxNameBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"The name field '{field}' is more than {ProtocolText.Number(ProtocolLimits.MaxNameBytes)} bytes; that is the limit.");
        }

        byte[] bytes;
        try
        {
            bytes = ProtocolConstants.StrictUtf8.GetBytes(name);
        }
        catch (EncoderFallbackException)
        {
            throw new ProtocolException(ProtocolError.InvalidText, $"The name field '{field}' contains an unpaired surrogate.");
        }

        Check(bytes, field);
        return bytes;
    }

    /// <summary>Decodes a name's wire bytes (its length already read and within the byte limit), refusing what the rule refuses.</summary>
    public static string Decode(ReadOnlySpan<byte> utf8, string field) => Check(utf8, field);

    /// <summary>
    /// Whether <paramref name="scalar"/> is refused in a name: the C0 and C1 controls and DEL,
    /// U+2028 and U+2029, U+FEFF, the twelve directional formatting characters of UAX #9, and the
    /// invisible format characters.
    /// </summary>
    public static bool IsRefused(int scalar) => scalar switch
    {
        <= 0x1F => true,
        >= 0x7F and <= 0x9F => true,
        0x00AD => true,
        0x061C => true,
        0x180E => true,
        >= 0x200B and <= 0x200F => true,
        >= 0x2028 and <= 0x2029 => true,
        >= 0x202A and <= 0x202E => true,
        >= 0x2060 and <= 0x2064 => true,
        >= 0x2066 and <= 0x206F => true,
        0xFEFF => true,
        >= 0xFFF9 and <= 0xFFFB => true,
        0xE0001 => true,
        >= 0xE0020 and <= 0xE007F => true,
        _ => false,
    };

    private static string Check(ReadOnlySpan<byte> utf8, string field)
    {
        if (utf8.Length == 0)
        {
            throw Empty(field);
        }

        if (utf8.Length > ProtocolLimits.MaxNameBytes)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"The name field '{field}' is {ProtocolText.Number(utf8.Length)} bytes; the limit is {ProtocolText.Number(ProtocolLimits.MaxNameBytes)}.");
        }

        string text;
        try
        {
            text = ProtocolConstants.StrictUtf8.GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            throw new ProtocolException(ProtocolError.InvalidText, $"The name field '{field}' is not valid UTF-8.");
        }

        var scalars = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsRefused(rune.Value))
            {
                throw new ProtocolException(ProtocolError.InvalidText, $"The name field '{field}' contains U+{rune.Value.ToString("X4", CultureInfo.InvariantCulture)}, which a name never holds.");
            }

            scalars++;
        }

        if (scalars > ProtocolLimits.MaxNameScalars)
        {
            throw new ProtocolException(ProtocolError.LimitExceeded, $"The name field '{field}' holds {ProtocolText.Number(scalars)} characters; the limit is {ProtocolText.Number(ProtocolLimits.MaxNameScalars)}.");
        }

        return text;
    }

    private static ProtocolException Empty(string field) =>
        new(ProtocolError.InvalidLength, $"The name field '{field}' is empty; a name holds at least one character.");
}
