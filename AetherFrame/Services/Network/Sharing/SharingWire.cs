using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>
/// A character's Lodestone id, read from the address of its Lodestone page as the player pastes it
/// (decision C2, step 2). Nothing is fetched: the plugin never contacts the Lodestone itself.
/// </summary>
internal static class LodestoneAddress
{
    private static readonly string[] Regions = ["na", "eu", "jp", "fr", "de"];

    /// <summary>
    /// Reads the id from <paramref name="text"/>: the character's page on any region's Lodestone
    /// (<c>https://na.finalfantasyxiv.com/lodestone/character/12345678/</c>, with or without the
    /// scheme, and with anything after the id), or the id alone.
    /// </summary>
    internal static bool TryReadId(string? text, out string id)
    {
        id = "";
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 256)
        {
            return false;
        }

        if (IsId(value))
        {
            id = value;
            return true;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !IsLodestoneHost(uri.Host))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || segments[0] != "lodestone" || segments[1] != "character" || !IsId(segments[2]))
        {
            return false;
        }

        id = segments[2];
        return true;
    }

    /// <summary>A Lodestone id as the server takes it: 1 to 10 decimal digits, with no leading zero.</summary>
    internal static bool IsId(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 10 || text[0] == '0')
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

        return true;
    }

    private static bool IsLodestoneHost(string host)
    {
        foreach (var region in Regions)
        {
            if (string.Equals(host, region + ".finalfantasyxiv.com", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The code the server issues for a Lodestone check (C2): <c>AF-</c> and 10 Crockford Base32 symbols.</summary>
internal static class LodestoneCode
{
    internal const int Length = 13;

    internal static bool IsCode(string? text)
    {
        if (text is null || text.Length != Length || !text.StartsWith("AF-", StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = 3; index < text.Length; index++)
        {
            var c = text[index];
            var digit = c is >= '0' and <= '9';
            var letter = c is >= 'A' and <= 'Z' && c is not ('I' or 'L' or 'O' or 'U');
            if (!digit && !letter)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>What a check's answer names: the binding's profile id, and the name and World the Lodestone showed.</summary>
internal sealed record CheckAnswer(ProfileId ProfileId, string Name, string World);

/// <summary>
/// The bodies the plugin sends and the answers it reads for the sharing server's actions
/// (docs/networking/ServerApi-v1.md, section 2.1). Bodies are written by a JSON writer, never by
/// hand. Answers are read strictly: exactly the properties the interface lists, each of its type,
/// every value checked, and anything else refused as an answer the plugin can't use.
/// </summary>
internal static class SharingWire
{
    internal static byte[] Empty() => "{}"u8.ToArray();

    internal static byte[] Pause() => Write(writer => writer.WriteString("mode", "pause"));

    /// <summary>
    /// A check's body: the Lodestone id, the code, and the name and World of the character the
    /// player is logged in as, which the server requires the page to show (N2-9b).
    /// </summary>
    internal static byte[] Check(string lodestoneId, string code, string name, string world)
    {
        if (!LodestoneAddress.IsId(lodestoneId) || !LodestoneCode.IsCode(code) || !SharingStateCodec.IsText(name) || !SharingStateCodec.IsText(world))
        {
            throw new ArgumentException("A check needs a Lodestone id, a code, and the character's name and World.");
        }

        return Write(writer =>
        {
            writer.WriteString("lodestoneId", lodestoneId);
            writer.WriteString("code", code);
            writer.WriteString("name", name);
            writer.WriteString("world", world);
        });
    }

    /// <summary>A code request's answer: the code, and how many seconds it lasts.</summary>
    /// <exception cref="InvalidDataException">The answer isn't one.</exception>
    internal static (string Code, int Seconds) ReadCode(byte[] body)
    {
        using var document = Parse(body, ["code", "expiresInSeconds"]);
        var code = String(document.RootElement, "code");
        var expires = document.RootElement.GetProperty("expiresInSeconds");
        if (!LodestoneCode.IsCode(code) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt32(out var seconds) || seconds is < 1 or > 86400)
        {
            throw new InvalidDataException("The server's code isn't one.");
        }

        return (code, seconds);
    }

    /// <summary>A check's answer.</summary>
    /// <exception cref="InvalidDataException">The answer isn't one.</exception>
    internal static CheckAnswer ReadCheck(byte[] body)
    {
        using var document = Parse(body, ["profileId", "name", "world"]);
        var root = document.RootElement;
        if (!ProfileId.TryParse(String(root, "profileId"), out var profileId))
        {
            throw new InvalidDataException("The server's profile id isn't one.");
        }

        return new CheckAnswer(profileId, Text(root, "name"), Text(root, "world"));
    }

    /// <summary>A re-read's answer: the name and World the Lodestone shows now.</summary>
    /// <exception cref="InvalidDataException">The answer isn't one.</exception>
    internal static (string Name, string World) ReadReread(byte[] body)
    {
        using var document = Parse(body, ["name", "world"]);
        return (Text(document.RootElement, "name"), Text(document.RootElement, "world"));
    }

    /// <summary>The server's status: its protocol version, its API version, and the oldest plugin it serves, exactly major.minor.build.</summary>
    /// <exception cref="InvalidDataException">The answer isn't one.</exception>
    internal static (int Protocol, int Api, Version MinimumPlugin) ReadStatus(byte[] body)
    {
        using var document = Parse(body, ["protocolVersion", "api", "minimumPlugin"]);
        var root = document.RootElement;
        var protocol = root.GetProperty("protocolVersion");
        var api = root.GetProperty("api");
        var parts = String(root, "minimumPlugin").Split('.');
        if (protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out var protocolVersion) || api.ValueKind != JsonValueKind.Number || !api.TryGetInt32(out var apiVersion)
            || parts.Length != 3 || !parts.All(part => part.Length is > 0 and <= 5 && part.All(c => c is >= '0' and <= '9')))
        {
            throw new InvalidDataException("The server's status isn't one.");
        }

        var number = Array.ConvertAll(parts, part => int.Parse(part, System.Globalization.CultureInfo.InvariantCulture));
        return (protocolVersion, apiVersion, new Version(number[0], number[1], number[2]));
    }

    private static byte[] Write(Action<Utf8JsonWriter> properties)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            properties(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static JsonDocument Parse(byte[] body, string[] names)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 2, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The server's answer isn't JSON.");
        }

        var root = document.RootElement;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var exact = root.ValueKind == JsonValueKind.Object;
        if (exact)
        {
            foreach (var property in root.EnumerateObject())
            {
                exact &= Array.IndexOf(names, property.Name) >= 0 && seen.Add(property.Name);
            }
        }

        if (!exact || seen.Count != names.Length)
        {
            document.Dispose();
            throw new InvalidDataException("The server's answer doesn't have the properties it should.");
        }

        return document;
    }

    private static string String(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("A property of the server's answer isn't text.");
    }

    private static string Text(JsonElement root, string name)
    {
        var text = String(root, name);
        return SharingStateCodec.IsText(text) ? text : throw new InvalidDataException("A name or World in the server's answer isn't one.");
    }
}
