using System;

namespace AetherFrame.Personas;

/// <summary>
/// The rule for a persona's label: the private name the player gives a persona so they can tell
/// their personas apart ("Main", "RP alt"). It is shown only to the player on this installation;
/// it is never published, never part of a persona's identity, and never written to a log by this
/// assembly. Surrounding whitespace is trimmed; what remains must be 1 to <see cref="MaxLength"/>
/// UTF-16 code units with no control characters. Nothing else is rewritten.
/// <para>
/// PROVISIONAL under D9a (docs/networking/DecisionRegister.md, "Persona display name"), which is
/// UNRESOLVED. This is the register's recommended option, a private local label only, implemented
/// so the in-memory model can be exercised; it is not an approval of that option. The rule, the
/// limit, and whether a label exists at all may change or go when the owner decides D9a.
/// </para>
/// </summary>
public static class PersonaLabel
{
    /// <summary>The longest label, in UTF-16 code units.</summary>
    public const int MaxLength = 64;

    /// <summary>Applies the rule: true and the trimmed label when <paramref name="text"/> is acceptable.</summary>
    public static bool TryNormalize(string? text, out string label)
    {
        label = "";
        if (text is null)
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > MaxLength)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        label = trimmed;
        return true;
    }

    /// <exception cref="PersonaException"><see cref="PersonaError.InvalidLabel"/>.</exception>
    internal static string Normalize(string? text) =>
        TryNormalize(text, out var label) ? label : throw new PersonaException(PersonaError.InvalidLabel, $"A persona label is 1 to {MaxLength} characters once trimmed, with no control characters.");
}
