using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherFrame.ReleaseTools;

/// <summary>
/// One way to read and one way to write JSON. Reading rejects comments, trailing commas, duplicate
/// and unknown keys, and lenient numbers, so a typo or a foreign file fails instead of being
/// half-understood. Writing is deterministic: two-space indentation, LF line ends, a trailing LF,
/// UTF-8 without a byte order mark, and fixed property order from the model classes.
/// </summary>
public static class StrictJson
{
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        // Quotes, backslashes and control characters are always escaped, which is all JSON needs;
        // everything else is written as it is so the files stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Parses a JSON object whose keys must all be known and unique.</summary>
    public static T ReadObject<T>(ReadOnlyMemory<byte> utf8, string what, IReadOnlyCollection<string> knownKeys, IReadOnlyDictionary<string, string>? explainedKeys = null)
    {
        using var document = ParseDocument(utf8, what);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ReleaseCheckException($"{what} must be a JSON object, not {Describe(document.RootElement.ValueKind)}.");
        }

        CheckKeys(document.RootElement, what, knownKeys, explainedKeys);
        return Deserialize<T>(utf8, what);
    }

    /// <summary>Parses a JSON array of objects whose keys must all be known and unique.</summary>
    public static List<T> ReadArrayOfObjects<T>(ReadOnlyMemory<byte> utf8, string what, IReadOnlyCollection<string> knownKeys)
    {
        using var document = ParseDocument(utf8, what);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ReleaseCheckException($"{what} must be a JSON array, not {Describe(document.RootElement.ValueKind)}.");
        }

        var index = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ReleaseCheckException($"{what} entry {index} must be a JSON object, not {Describe(element.ValueKind)}.");
            }

            CheckKeys(element, $"{what} entry {index}", knownKeys, null);
            index++;
        }

        return Deserialize<List<T>>(utf8, what);
    }

    public static JsonDocument ParseDocument(ReadOnlyMemory<byte> utf8, string what)
    {
        try
        {
            return JsonDocument.Parse(utf8, DocumentOptions);
        }
        catch (JsonException e)
        {
            throw new ReleaseCheckException($"{what} is not valid JSON: {e.Message}");
        }
    }

    public static byte[] Serialize<T>(T value)
    {
        var text = JsonSerializer.Serialize(value, WriteOptions) + "\n";
        return new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
    }

    private static T Deserialize<T>(ReadOnlyMemory<byte> utf8, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(utf8.Span, ReadOptions) ?? throw new ReleaseCheckException($"{what} is JSON null.");
        }
        catch (JsonException e)
        {
            throw new ReleaseCheckException($"{what} has a value of the wrong type: {e.Message}");
        }
    }

    private static void CheckKeys(JsonElement element, string what, IReadOnlyCollection<string> knownKeys, IReadOnlyDictionary<string, string>? explainedKeys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new ReleaseCheckException($"{what} has the key '{property.Name}' more than once.");
            }

            if (!knownKeys.Contains(property.Name))
            {
                unknown.Add(property.Name);
            }
        }

        if (unknown.Count > 0)
        {
            var explanations = explainedKeys is null
                ? string.Empty
                : string.Concat(unknown.Where(explainedKeys.ContainsKey).Select(k => $" {explainedKeys[k]}"));
            throw new ReleaseCheckException($"{what} has unknown key(s): {string.Join(", ", unknown)}.{explanations}");
        }
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => kind.ToString(),
    };
}
