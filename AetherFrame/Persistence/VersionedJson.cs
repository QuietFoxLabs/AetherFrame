using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Persistence.Schema;

namespace AetherFrame.Persistence;

/// <summary>The result of reading one versioned JSON object.</summary>
internal sealed class VersionedReadResult<T>
    where T : class
{
    internal VersionedReadResult(SchemaMigrationResult migration, JsonObject? raw, T? value, bool recoveredFromBackup = false, bool hasInvalidBytes = false)
    {
        Migration = migration;
        Raw = raw;
        Value = value;
        RecoveredFromBackup = recoveredFromBackup;
        HasInvalidBytes = hasInvalidBytes;
    }

    internal SchemaMigrationResult Migration { get; }

    /// <summary>Usable: the migrated JSON. NewerVersion: the JSON exactly as read. Invalid: null.</summary>
    internal JsonObject? Raw { get; }

    /// <summary>The typed object; only for usable (current or migrated) results.</summary>
    internal T? Value { get; }

    /// <summary>True when the copy on disk was unusable and this came from the store's backup
    /// copy instead (see <see cref="VersionedJson.ReadAsync{T}"/>).</summary>
    internal bool RecoveredFromBackup { get; }

    /// <summary>True when the copy this came from held bytes that aren't valid text (see
    /// <see cref="StoredText.HasInvalidBytes"/>): it was read as it is, with U+FFFD in their place,
    /// so its original bytes must be kept before anything writes over the file.</summary>
    internal bool HasInvalidBytes { get; }

    internal bool IsUsable => Migration.IsUsable && Value is not null;

    internal bool IsNewerVersion => Migration.Outcome == SchemaMigrationOutcome.NewerVersion;

    internal bool WasMigrated => Migration.Outcome == SchemaMigrationOutcome.Migrated;
}

/// <summary>Parsing, schema migration, and typed reading of versioned JSON files.</summary>
internal static class VersionedJson
{
    /// <summary>
    /// Parses <paramref name="json"/>, migrates it in memory, and deserializes it. Never throws for
    /// bad content — an unusable object comes back as <see cref="SchemaMigrationOutcome.Invalid"/>.
    /// </summary>
    internal static VersionedReadResult<T> Parse<T>(string json, SchemaDefinition schema, Func<JsonObject, T?>? deserialize = null)
        where T : class
    {
        JsonObject raw;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed)
            {
                return Invalid<T>($"{schema.Name} is not a JSON object.");
            }

            // JsonNode.Parse defers detecting duplicate property names until the object is
            // materialized, so materialize it here where that is still "not valid JSON".
            _ = parsed.Count;
            raw = parsed;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return Invalid<T>($"{schema.Name} is not valid JSON: {ex.Message}");
        }

        var migration = schema.Migrate(raw);
        if (migration.Outcome == SchemaMigrationOutcome.NewerVersion)
        {
            return new VersionedReadResult<T>(migration, raw, null);
        }

        if (!migration.IsUsable)
        {
            return new VersionedReadResult<T>(migration, null, null);
        }

        try
        {
            var value = deserialize is not null ? deserialize(raw) : raw.Deserialize<T>(JsonOptions.Default);
            return value is null
                ? Invalid<T>($"{schema.Name} is empty.")
                : new VersionedReadResult<T>(migration, raw, value);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException)
        {
            return Invalid<T>($"{schema.Name} has unreadable content: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads and parses a file through <paramref name="store"/>. Invalid content throws inside the
    /// store's reader so a store with backups retries from its backup copy; a NEWER-version file
    /// deliberately does not, because falling back to an older backup of it would silently hand
    /// back stale data that a later save could then write over the newer file. Throws
    /// <see cref="InvalidDataException"/> (or the IO error) when no usable copy exists. A result
    /// the store had to retry for reports <see cref="VersionedReadResult{T}.RecoveredFromBackup"/>.
    /// </summary>
    /// <remarks>
    /// The encoding never decides which copy is used. Bytes that aren't valid text are read as
    /// U+FFFD, as every earlier version read them, and the result says so
    /// (<see cref="VersionedReadResult{T}.HasInvalidBytes"/>) instead of being refused: refusing
    /// would read an older backup in place of a file that parses (losing whatever is newer in it,
    /// or a newer version's protection), or, with no backup, make the file unreadable, and a
    /// damaged binding or index is then replaced. Only content that isn't usable falls back.
    /// </remarks>
    internal static async Task<VersionedReadResult<T>> ReadAsync<T>(IPlateFileStore store, string path, SchemaDefinition schema, Func<JsonObject, T?>? deserialize = null)
        where T : class
    {
        VersionedReadResult<T>? result = null;
        var attempts = 0;

        await store.ReadTextAsync(path, stored =>
        {
            attempts++;
            var parsed = Parse(stored.Text, schema, deserialize);
            if (!parsed.IsUsable && !parsed.IsNewerVersion)
            {
                throw new InvalidDataException(parsed.Migration.Error ?? $"{schema.Name} is unreadable.");
            }

            // A second invocation is the store retrying from its backup after the first copy failed.
            result = new VersionedReadResult<T>(parsed.Migration, parsed.Raw, parsed.Value, recoveredFromBackup: attempts > 1, stored.HasInvalidBytes);
        }).ConfigureAwait(false);

        return result ?? throw new InvalidDataException($"{schema.Name} could not be read.");
    }

    /// <summary>
    /// The text a write of <paramref name="json"/> reads back as at the next load (see
    /// <see cref="StoredTextDecoder.ReadBack"/>), refused as <see cref="InvalidDataException"/>
    /// when that isn't exactly <paramref name="json"/>: what the Library keeps in memory must be
    /// what the file holds. Only text holding a lone surrogate or starting with U+FEFF could fail
    /// this, and the serializer's output is neither (it escapes everything outside ASCII and starts
    /// with a brace); such text is refused before anything is written.
    /// </summary>
    internal static string RequireFaithfulReadBack(string json, string what)
    {
        var readBack = StoredTextDecoder.ReadBack(json);
        if (readBack.HasInvalidBytes || !string.Equals(readBack.Text, json, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{what} would not read back as the text written.");
        }

        return readBack.Text;
    }

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions.Default);

    internal static string Serialize(JsonObject raw) => raw.ToJsonString(JsonOptions.Default);

    private static VersionedReadResult<T> Invalid<T>(string error)
        where T : class =>
        new(new SchemaMigrationResult(SchemaMigrationOutcome.Invalid, 0, 0, error), null, null);
}
