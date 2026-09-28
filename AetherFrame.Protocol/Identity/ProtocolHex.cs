using System;

namespace AetherFrame.Protocol.Identity;

/// <summary>
/// The text form of identifiers: lowercase hexadecimal only. Parsing is strict so an identifier
/// has exactly one text form (no uppercase, no whitespace, no prefix variations).
/// </summary>
internal static class ProtocolHex
{
    public static string ToLower(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>Parses exactly <c>destination.Length * 2</c> lowercase hex digits.</summary>
    public static bool TryParseLower(ReadOnlySpan<char> text, Span<byte> destination)
    {
        if (text.Length != destination.Length * 2)
        {
            return false;
        }

        for (var index = 0; index < destination.Length; index++)
        {
            var high = Digit(text[index * 2]);
            var low = Digit(text[(index * 2) + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            destination[index] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static int Digit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };
}
