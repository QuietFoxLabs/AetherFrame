using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AetherFrame.Services.Packages;

/// <summary>Why <see cref="PackageJson.TryParseObject"/> refused a document.</summary>
internal enum PackageJsonProblemKind
{
    /// <summary>Not strict, well-formed JSON; not an object; or ambiguous (a property repeated within one object).</summary>
    Malformed,

    /// <summary>Well-formed, but past <see cref="PackagePolicy.MaxJsonValueCount"/> or <see cref="PackagePolicy.MaxJsonPropertyNameLength"/>.</summary>
    TooLarge,
}

/// <param name="Detail">Diagnostic detail for the log: positions, counts, safe names — never content.</param>
internal sealed record PackageJsonProblem(PackageJsonProblemKind Kind, string Detail);

/// <summary>
/// The only way package JSON is parsed: strict (no comments, no trailing commas), depth-limited
/// by the parser itself (so deep nesting fails before it can recurse), and into plain JSON nodes
/// — never into types named by the data. Typed reading happens afterwards, through the
/// document's own fixed, compile-time list of element types.
///
/// <para>Before any tree is built, one forward pass over the tokens screens the document: a
/// property repeated within an object, more values than <see cref="PackagePolicy.MaxJsonValueCount"/>,
/// or a property name longer than <see cref="PackagePolicy.MaxJsonPropertyNameLength"/> refuses it
/// at the cost of a scan (nothing allocated per value), never at the cost of the tree — every
/// later stage walks or copies the whole tree, which is what the value limit protects.</para>
/// </summary>
internal static class PackageJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = PackagePolicy.MaxJsonDepth,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    /// <summary>Stands in for an object's name set until its first property, so an empty object costs no set.</summary>
    private static readonly HashSet<string> NoNamesYet = new(StringComparer.Ordinal);

    /// <summary>Parses UTF-8 JSON that must be an object. Never throws for bad content.</summary>
    internal static bool TryParseObject(ReadOnlySpan<byte> utf8Json, out JsonObject? result, out PackageJsonProblem? problem)
    {
        result = null;
        problem = null;

        // A UTF-8 byte order mark is tolerated (some editors add one); anything else is JSON's job.
        if (utf8Json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            utf8Json = utf8Json[3..];
        }

        try
        {
            if (Screen(utf8Json) is { } refused)
            {
                problem = refused;
                return false;
            }

            var node = JsonNode.Parse(utf8Json, NodeOptions, DocumentOptions);
            if (node is not JsonObject obj)
            {
                problem = new PackageJsonProblem(PackageJsonProblemKind.Malformed, "not a JSON object");
                return false;
            }

            // JsonObject builds itself lazily; build it here, inside the try, not on first use later.
            _ = obj.Count;
            result = obj;
            return true;
        }
        catch (JsonException ex)
        {
            problem = new PackageJsonProblem(PackageJsonProblemKind.Malformed, $"malformed JSON at line {ex.LineNumber}, byte {ex.BytePositionInLine}");
            return false;
        }
        catch (ArgumentException)
        {
            problem = new PackageJsonProblem(PackageJsonProblemKind.Malformed, "malformed JSON");
            return false;
        }
        catch (InvalidOperationException)
        {
            problem = new PackageJsonProblem(PackageJsonProblemKind.Malformed, "malformed JSON");
            return false;
        }
    }

    /// <summary>
    /// The forward pass: the first property name repeated within one object, the value count and
    /// every property name's length, in one scan whose reader also enforces the depth limit. Null
    /// when the document passes; throws <see cref="JsonException"/> for malformed JSON, as the
    /// parser would.
    /// </summary>
    private static PackageJsonProblem? Screen(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = PackagePolicy.MaxJsonDepth });
        var scopes = new Stack<HashSet<string>?>();
        var values = 0;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(NoNamesYet);
                    values++;
                    break;
                case JsonTokenType.StartArray:
                    scopes.Push(null);
                    values++;
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    scopes.Pop();
                    continue;
                case JsonTokenType.PropertyName:
                    var name = reader.GetString()!;
                    if (name.Length > PackagePolicy.MaxJsonPropertyNameLength)
                    {
                        return new PackageJsonProblem(PackageJsonProblemKind.TooLarge, $"a property name is longer than {PackagePolicy.MaxJsonPropertyNameLength} characters");
                    }

                    // JsonNode keeps the last of two same-named properties; an ambiguous object is refused.
                    var names = scopes.Peek();
                    if (ReferenceEquals(names, NoNamesYet))
                    {
                        scopes.Pop();
                        names = new HashSet<string>(StringComparer.Ordinal);
                        scopes.Push(names);
                    }

                    if (!names!.Add(name))
                    {
                        return new PackageJsonProblem(PackageJsonProblemKind.Malformed, $"duplicate property \"{PackageManifest.SafeForLog(name)}\"");
                    }

                    continue;
                default:
                    values++;
                    break;
            }

            if (values > PackagePolicy.MaxJsonValueCount)
            {
                return new PackageJsonProblem(PackageJsonProblemKind.TooLarge, $"more than {PackagePolicy.MaxJsonValueCount} JSON values");
            }
        }

        return null;
    }
}
