using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AetherFrame.Server.Lodestone;

/// <summary>A Lodestone character id (decision C2): 1 to 10 decimal digits with no leading zero.</summary>
internal static class LodestoneIds
{
    public static bool TryParse(string? text, out long id)
    {
        id = 0;
        if (text is null || text.Length is < 1 or > 10 || text[0] == '0')
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        id = long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }
}

/// <summary>
/// The Worlds a character can be on (decision C2): the Lodestone shows a Home World by its name,
/// the same in every client language, and a name not on this list fails a check. The operator adds a
/// new World through configuration until a release adds it here.
/// </summary>
internal sealed class Worlds
{
    private static readonly string[] BuiltIn =
    [
        // North America: Aether, Crystal, Dynamis, Primal.
        "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren",
        "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera",
        "Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph",
        "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros",

        // Europe: Chaos, Light, Shadow.
        "Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan",
        "Alpha", "Lich", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark",
        "Innocence", "Pixie", "Titania", "Tycoon",

        // Oceania: Materia.
        "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan",

        // Japan: Elemental, Gaia, Mana, Meteor.
        "Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Tonberry", "Typhon",
        "Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima",
        "Anima", "Asura", "Chocobo", "Hades", "Ixion", "Masamune", "Pandaemonium", "Titan",
        "Belias", "Mandragora", "Ramuh", "Shinryu", "Unicorn", "Valefor", "Yojimbo", "Zeromus",
    ];

    private readonly Dictionary<string, string> byKey;

    public Worlds(IEnumerable<string> added)
    {
        byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var world in BuiltIn.Concat(added))
        {
            byKey.TryAdd(world, world);
        }
    }

    /// <summary>Whether <paramref name="world"/> could be a World's name: 3 to 20 ASCII letters.</summary>
    public static bool IsWellFormed(string? world) =>
        world is { Length: >= 3 and <= 20 } && world.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    /// <summary>The World named <paramref name="text"/>, matched without regard to case, in the list's spelling.</summary>
    public bool TryFind(string? text, out string world)
    {
        world = "";
        if (!IsWellFormed(text) || !byKey.TryGetValue(text!, out var found))
        {
            return false;
        }

        world = found;
        return true;
    }
}

/// <summary>Character names (decision C1).</summary>
internal static class CharacterNames
{
    /// <summary>The longest forename or surname the game allows.</summary>
    public const int MaxPartLength = 15;

    /// <summary>The longest full name the game allows, the space included.</summary>
    public const int MaxLength = 21;

    /// <summary>
    /// Whether <paramref name="name"/> is a name the game allows, as the Lodestone shows one: two
    /// words separated by one space, each starting with a capital letter and holding only ASCII
    /// letters, an apostrophe or a hyphen, within the game's lengths.
    /// </summary>
    public static bool IsGameName(string? name)
    {
        if (name is null || name.Length > MaxLength)
        {
            return false;
        }

        var parts = name.Split(' ');
        return parts.Length == 2 && parts.All(IsPart);
    }

    /// <summary>
    /// The form names are matched in: Unicode NFC, then invariant lower case, with runs of spaces
    /// folded to one and none at either end. Null for text that could never be a name.
    /// </summary>
    public static string? Key(string? text)
    {
        if (text is null || text.Length > 256)
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }

        var builder = new StringBuilder(normalized.Length);
        var space = false;
        foreach (var c in normalized.Trim(' '))
        {
            if (c == ' ')
            {
                space = true;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool IsPart(string part) =>
        part.Length is >= 2 and <= MaxPartLength
        && part[0] is >= 'A' and <= 'Z'
        && part.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '\'' or '-');
}

/// <summary>
/// The Lodestone check's code (decision C2): <c>AF-</c> and 10 Crockford Base32 symbols, 50 bits
/// from the CSPRNG.
/// </summary>
internal static class LodestoneCodes
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const int Symbols = 10;

    public static string NewCode()
    {
        Span<byte> bytes = stackalloc byte[Symbols];
        RandomNumberGenerator.Fill(bytes);
        var builder = new StringBuilder("AF-", 3 + Symbols);
        foreach (var b in bytes)
        {
            builder.Append(Alphabet[b & 31]);
        }

        return builder.ToString();
    }

    /// <summary>Whether <paramref name="text"/> has a code's exact form.</summary>
    public static bool IsWellFormed(string? text) =>
        text is { Length: 3 + Symbols } && text.StartsWith("AF-", StringComparison.Ordinal) && text.AsSpan(3).IndexOfAnyExcept(Alphabet) < 0;
}
