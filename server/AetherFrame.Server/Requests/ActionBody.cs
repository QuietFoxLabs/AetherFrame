using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AetherFrame.Server.Requests;

/// <summary>
/// An action's body (ServerApi-v1.md, section 2.1): one JSON object whose properties are exactly the
/// ones named, strings unless named as numbers. An unknown, missing or repeated property, a value of
/// the wrong type, a nested value, a comment or anything after the object refuses the body.
/// </summary>
internal sealed class ActionBody
{
    private readonly Dictionary<string, string> strings;
    private readonly Dictionary<string, long> numbers;

    private ActionBody(Dictionary<string, string> strings, Dictionary<string, long> numbers)
    {
        this.strings = strings;
        this.numbers = numbers;
    }

    public string String(string name) => strings[name];

    public long Number(string name) => numbers[name];

    /// <summary>Reads <paramref name="body"/>, or null when it breaks any rule above.</summary>
    public static ActionBody? Read(ReadOnlySpan<byte> body, IReadOnlyCollection<string> stringNames, IReadOnlyCollection<string>? numberNames = null)
    {
        numberNames ??= [];
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 2 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return null;
                }

                var name = reader.GetString()!;
                if (!reader.Read() || strings.ContainsKey(name) || numbers.ContainsKey(name))
                {
                    return null;
                }

                if (Contains(stringNames, name) && reader.TokenType == JsonTokenType.String)
                {
                    strings[name] = reader.GetString()!;
                }
                else if (Contains(numberNames, name) && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number))
                {
                    numbers[name] = number;
                }
                else
                {
                    return null;
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return strings.Count == stringNames.Count && numbers.Count == numberNames.Count ? new ActionBody(strings, numbers) : null;
    }

    private static bool Contains(IReadOnlyCollection<string> names, string name)
    {
        foreach (var candidate in names)
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
