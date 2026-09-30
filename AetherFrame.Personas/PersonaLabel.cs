using System;

namespace AetherFrame.Personas;

/// <summary>
/// The rule for a persona's label: the private name the player gives a persona so they can tell
/// their personas apart ("Main", "RP alt"). It is shown only to the player on this installation;
/// it is never published, never part of a persona's identity, and never written to a log by this
/// assembly. Surrounding whitespace is trimmed; what remains must be 1 to <see cref="MaxLength"/>
/// UTF-16 code units with no control characters and no unpaired surrogates, so that a label
/// survives the registry's round trip exactly. Nothing else is rewritten.
/// <para>
/// The rule never changes with the runtime: the registry refuses a label that isn't its own
/// normalization (P3), so a rule that moved would make a saved registry unreadable. The whitespace
/// it trims is therefore a fixed list rather than the runtime's table, and control characters and
/// surrogates are ranges Unicode never changes. Changing the rule needs a new registry version.
/// </para>
/// <para>
/// Decision D9a (docs/networking/DecisionRegister.md) approved this option: a private local label
/// only, never in any document, request, record or log.
/// </para>
/// </summary>
public static class PersonaLabel
{
    /// <summary>The longest label, in UTF-16 code units.</summary>
    public const int MaxLength = 64;

    // Unicode's White_Space characters, as listed since Unicode 6.3: the 25 code points that .NET's
    // string.Trim() removes today, fixed here so no runtime update can change which labels are valid.
    private static readonly char[] Whitespace =
    [
        (char)0x0009, (char)0x000A, (char)0x000B, (char)0x000C, (char)0x000D, (char)0x0020, (char)0x0085, (char)0x00A0, (char)0x1680,
        (char)0x2000, (char)0x2001, (char)0x2002, (char)0x2003, (char)0x2004, (char)0x2005, (char)0x2006, (char)0x2007, (char)0x2008,
        (char)0x2009, (char)0x200A, (char)0x2028, (char)0x2029, (char)0x202F, (char)0x205F, (char)0x3000,
    ];

    /// <summary>Applies the rule: true and the trimmed label when <paramref name="text"/> is acceptable.</summary>
    public static bool TryNormalize(string? text, out string label)
    {
        label = "";
        if (text is null)
        {
            return false;
        }

        var trimmed = text.Trim(Whitespace);
        if (trimmed.Length is 0 or > MaxLength)
        {
            return false;
        }

        for (var index = 0; index < trimmed.Length; index++)
        {
            var c = trimmed[index];
            if (char.IsControl(c))
            {
                return false;
            }

            if (char.IsHighSurrogate(c) && index + 1 < trimmed.Length && char.IsLowSurrogate(trimmed[index + 1]))
            {
                index++;
            }
            else if (char.IsSurrogate(c))
            {
                return false;
            }
        }

        label = trimmed;
        return true;
    }

    /// <exception cref="PersonaException"><see cref="PersonaError.InvalidLabel"/>.</exception>
    internal static string Normalize(string? text) =>
        TryNormalize(text, out var label) ? label : throw new PersonaException(PersonaError.InvalidLabel, $"A persona label is 1 to {MaxLength} characters once trimmed, with no control characters and no unpaired surrogates.");
}
