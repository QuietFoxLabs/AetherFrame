using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence.Schema;

namespace AetherFrame.Persistence;

/// <summary>What reading a kept draft's text found (see <see cref="DraftDocuments.Parse"/>).</summary>
internal enum DraftTextStatus
{
    /// <summary>Usable by this build.</summary>
    Ready,

    /// <summary>
    /// Written by a newer AetherFrame (its envelope, or the Plate document inside it): never
    /// restored, rewritten or offered by this build, only moved intact if ever retired.
    /// </summary>
    NewerVersion,
}

/// <summary>
/// One draft as read. Ready: the envelope, its document with the in-memory repairs a load applies,
/// and the document's JSON as migrated (every field kept, unknown ones included). NewerVersion: the
/// JSON exactly as read, nothing else.
/// </summary>
internal sealed record DraftText(DraftTextStatus Status, JsonObject Raw, PlateDraft? Draft, string? DocumentJson);

/// <summary>
/// Turning a kept draft's JSON into a <see cref="PlateDraft"/> and back: <see cref="TemplateDocuments"/>'s
/// two-layer pattern, the envelope (<see cref="PersistenceSchemas.Draft"/>) around a Plate document
/// that keeps its own schema (<see cref="PersistenceSchemas.ProfileDocument"/>) and is read through
/// the one path every Plate is read (<see cref="PlateDocuments"/>), so unknown elements and fields
/// survive.
/// </summary>
internal static class DraftDocuments
{
    /// <summary>
    /// Reads already-migrated JSON, as <see cref="TemplateDocuments.Deserialize"/> does: null for a
    /// malformed envelope. An editor this build doesn't know reads as <see cref="DraftEditor.None"/>.
    /// <paramref name="raw"/> itself is never modified.
    /// </summary>
    internal static PlateDraft? Deserialize(JsonObject raw)
    {
        if (raw[nameof(PlateDraft.Document)] is not JsonObject documentRaw)
        {
            return null;
        }

        var document = PlateDocuments.Deserialize(documentRaw);
        if (document is null)
        {
            return null;
        }

        var withoutDocument = (JsonObject)raw.DeepClone();
        withoutDocument.Remove(nameof(PlateDraft.Document));

        var draft = withoutDocument.Deserialize<PlateDraft>(JsonOptions.Default);
        if (draft is null)
        {
            return null;
        }

        draft.Document = document;
        if (!Enum.IsDefined(draft.Editor))
        {
            draft.Editor = DraftEditor.None;
        }

        return draft;
    }

    /// <summary>Serializes the envelope with its document written exactly as a Plate's is (see <see cref="PlateDocuments.ToJson"/>).</summary>
    internal static JsonObject ToJson(PlateDraft draft)
    {
        var json = JsonSerializer.SerializeToNode(draft, JsonOptions.Default) as JsonObject
            ?? throw new JsonException("Unsaved changes serialized to something other than an object.");

        json[nameof(PlateDraft.Document)] = PlateDocuments.ToJson(draft.Document);
        return json;
    }

    /// <summary>
    /// Reads one draft file's text, both layers migrated: damage throws <see cref="InvalidDataException"/>
    /// (so a store with backups retries from its backup copy, which for a draft can only hold what was
    /// written under that same unique name), and a newer envelope or document comes back as
    /// <see cref="DraftTextStatus.NewerVersion"/>, untouched. Also how a write proves its text loads.
    /// A draft must name its Plate, and its document must be that Plate's.
    /// </summary>
    internal static DraftText Parse(string text)
    {
        JsonObject raw;
        try
        {
            raw = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("Unsaved changes are not a JSON object.");

            // Duplicate property names are detected only once the object is materialized.
            _ = raw.Count;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Unsaved changes are not valid JSON: {ex.Message}", ex);
        }

        var envelope = PersistenceSchemas.Draft.Migrate(raw);
        if (envelope.Outcome == SchemaMigrationOutcome.NewerVersion)
        {
            return new DraftText(DraftTextStatus.NewerVersion, raw, null, null);
        }

        if (!envelope.IsUsable)
        {
            throw new InvalidDataException(envelope.Error ?? "Unsaved changes are unreadable.");
        }

        if (raw[nameof(PlateDraft.Document)] is not JsonObject documentRaw)
        {
            throw new InvalidDataException("Unsaved changes hold no Plate.");
        }

        var document = PersistenceSchemas.ProfileDocument.Migrate(documentRaw);
        if (document.Outcome == SchemaMigrationOutcome.NewerVersion)
        {
            return new DraftText(DraftTextStatus.NewerVersion, raw, null, null);
        }

        if (!document.IsUsable)
        {
            throw new InvalidDataException(document.Error ?? "The Plate in unsaved changes is unreadable.");
        }

        PlateDraft draft;
        try
        {
            draft = Deserialize(raw) ?? throw new InvalidDataException("Unsaved changes are empty.");
            draft.Document = PlateDocuments.Materialize(documentRaw);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new InvalidDataException($"Unsaved changes have unreadable content: {ex.Message}", ex);
        }

        if (draft.PlateId == Guid.Empty || draft.Document.ProfileId != draft.PlateId)
        {
            throw new InvalidDataException("Unsaved changes don't name the Plate they belong to.");
        }

        return new DraftText(DraftTextStatus.Ready, raw, draft, VersionedJson.Serialize(documentRaw));
    }

    /// <summary>
    /// The draft to keep for an open document: a copy made as a save makes its snapshot (see
    /// <c>ProfileService.CopyOpenDocument</c>), at the current schema version, with no character's
    /// Content ID (a legacy owner is a binding's business, and restoring takes it from the saved Plate).
    /// </summary>
    internal static PlateDraft Create(ProfileDocument copy, int baseRevision, DateTime baseUpdatedAtUtc, DraftEditor editor, string build, Guid draftId, DateTime nowUtc)
    {
        copy.Version = ProfileDocument.CurrentSchemaVersion;
        copy.OwnerContentId = 0;
        return new PlateDraft
        {
            Version = PlateDraft.CurrentSchemaVersion,
            DraftId = draftId,
            PlateId = copy.ProfileId,
            PlateName = copy.Name,
            WrittenAtUtc = nowUtc,
            Editor = editor,
            Build = build,
            BaseRevision = baseRevision,
            BaseUpdatedAtUtc = baseUpdatedAtUtc,
            Document = copy,
        };
    }
}
