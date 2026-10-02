using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Persistence;
using AetherFrame.Services;
using AetherFrame.Services.Plates;
using AetherFrame.UI.Editor;
using AetherFrame.UI.Library;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Reading the unsaved changes kept at unload: the version 1 format pinned by a file, names parsed
/// strictly, the newest twenty read, and a draft that is damaged or a newer version's left exactly
/// as it is while the others are still offered. Unknown data survives either way of restoring.
/// </summary>
public class KeptChangesReadTests
{
    private static readonly Guid FixturePlate = Guid.Parse("0f0f0f0f-3333-4333-8333-000000000301");
    private static readonly Guid FixtureDraft = Guid.Parse("0f0f0f0f-4444-4444-8444-000000000401");
    private static readonly Guid FixtureTextElement = Guid.Parse("0f0f0f0f-5555-4555-8555-000000000501");
    private static readonly Guid FixtureAsset = Guid.Parse("0f0f0f0f-6666-4666-8666-000000000601");
    private static readonly DateTime FixtureWritten = new(2026, 10, 2, 14, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime FixtureBase = new(2026, 10, 1, 9, 8, 7, DateTimeKind.Utc);

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "DraftFixtures", "draft-v1.json");

    private static string FixtureText() => HistoricalFixtures.ExactText(FixturePath);

    // ---------------------------------------------------------------- the pinned format

    [Fact]
    public void PinnedDraftV1_ReadsEveryField()
    {
        var text = DraftDocuments.Parse(FixtureText());

        Assert.Equal(DraftTextStatus.Ready, text.Status);
        var draft = text.Draft!;
        Assert.Equal(1, draft.Version);
        Assert.Equal(FixtureDraft, draft.DraftId);
        Assert.Equal(FixturePlate, draft.PlateId);
        Assert.Equal("Evening Look", draft.PlateName);
        Assert.Equal(FixtureWritten, draft.WrittenAtUtc);
        Assert.Equal(DraftEditor.Basic, draft.Editor);
        Assert.Equal("AetherFrame 0.1.9 (build d7d20b6) [sharing]", draft.Build);
        Assert.Equal(3, draft.BaseRevision);
        Assert.Equal(FixtureBase, draft.BaseUpdatedAtUtc);
        Assert.Equal(FixturePlate, draft.Document.ProfileId);
        Assert.Contains(draft.Document.Elements, e => e.Id == FixtureTextElement && e is TextProfileElement { Text: "Kept in the draft" });
        Assert.Contains(draft.Document.Elements, e => e is ImageProfileElement { AssetId: { } asset } && asset == FixtureAsset);
        Assert.Equal(1, draft.Document.UnsupportedElementCount);
        Assert.True(draft.Document.ExtensionData!.ContainsKey("FutureField"));
        Assert.Contains("hologram", text.DocumentJson, StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedDraftV1_RoundTripsByteForByte_ThroughTheWriter()
    {
        var original = FixtureText();
        var raw = JsonNode.Parse(original)!.AsObject();

        Assert.Equal(original, HistoricalFixtures.NormalizeLineEndings(VersionedJson.Serialize(raw)));
        Assert.Equal(original, HistoricalFixtures.NormalizeLineEndings(VersionedJson.Serialize(DraftDocuments.ToJson(DraftDocuments.Deserialize(raw)!))));
    }

    [Fact]
    public async Task PinnedDraftV1_IsOfferedAndRestored_OverTheVersionItStartedFrom()
    {
        using var fixture = new LibraryFixture();
        WriteSavedVersionOfFixture(fixture);
        var path = KeptFiles.WriteDraftJson(fixture.Paths, FixturePlate, FixtureWritten, FixtureDraft, FixtureText());
        var game = await GameSession.StartAsync(fixture);

        var found = Assert.Single(await game.LoadKeptChangesAsync());
        Assert.Equal(KeptChangesChoice.Restore, found.Choice);
        Assert.Equal(path, found.Path);

        game.Offer.Choose();

        Assert.Equal(FixturePlate, game.Profiles.OpenPlateId);
        Assert.Contains(game.Document.Elements, e => e.Id == FixtureTextElement);
        Assert.True(game.Session.IsDirty);
        Assert.Equal(new[] { EditorSurfaceKind.Basic }, game.Shown);
    }

    // ---------------------------------------------------------------- names

    [Fact]
    public void DraftNames_AreParsedStrictly()
    {
        var paths = new PlateStoragePaths(Path.Combine(Path.GetTempPath(), "names"));
        var plateId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var written = new DateTime(2026, 10, 2, 1, 2, 3, 4, DateTimeKind.Utc);
        var path = paths.GetDraftPath(plateId, written, draftId);

        Assert.Equal($"{plateId}.unsaved-20261002-010203-004-{draftId:N}.json", Path.GetFileName(path));
        Assert.Equal(paths.DraftsDirectory, Path.GetDirectoryName(path));
        Assert.True(PlateStoragePaths.TryParseDraftFileName(path, out var parsedPlate, out var parsedWritten, out var parsedDraft));
        Assert.Equal((plateId, written, draftId), (parsedPlate, parsedWritten, parsedDraft));
        Assert.Equal(DateTimeKind.Utc, parsedWritten.Kind);
        Assert.True(PlateStoragePaths.TryParseDraftFileName(path.ToUpperInvariant().Replace(".JSON", ".json", StringComparison.Ordinal), out _, out _, out _));

        var name = Path.GetFileName(path);
        foreach (var other in new[]
        {
            name + ".tmp",
            "." + name + "." + Guid.NewGuid().ToString("N") + ".tmp",
            name.Replace(".json", ".txt", StringComparison.Ordinal),
            name.Replace(".unsaved-", ".saved-", StringComparison.Ordinal),
            name.Replace("-004-", "-04-", StringComparison.Ordinal),
            name.Replace("20261002", "20261302", StringComparison.Ordinal),
            name.Replace(draftId.ToString("N"), new string('0', 32), StringComparison.Ordinal),
            name.Replace(draftId.ToString("N"), draftId.ToString("D"), StringComparison.Ordinal),
            name.Replace(plateId.ToString("D"), plateId.ToString("N"), StringComparison.Ordinal),
            name.Replace(plateId.ToString("D"), Guid.Empty.ToString("D"), StringComparison.Ordinal),
            " " + name,
            $"{plateId}.json",
        })
        {
            Assert.False(PlateStoragePaths.TryParseDraftFileName(Path.Combine(paths.DraftsDirectory, other), out _, out _, out _), other);
        }
    }

    [Fact]
    public async Task FilesThatAreNotDrafts_AreIgnored_AndLeftAsTheyAre()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync());
        game.Edit();
        await game.UnloadAsync();
        var draft = Assert.Single(KeptFiles.Drafts(fixture.Paths));
        File.WriteAllText(draft + ".tmp", "partial");
        File.WriteAllText(Path.Combine(fixture.Paths.DraftsDirectory, "notes.json"), "{}");
        var before = KeptFiles.Snapshot(fixture.Paths.DraftsDirectory);

        var listing = await game.Drafts.ReadNewestAsync();

        Assert.Equal(draft, Assert.Single(listing.Drafts).Path);
        Assert.Equal(before, KeptFiles.Snapshot(fixture.Paths.DraftsDirectory));
    }

    [Fact]
    public async Task OnlyTheNewestTwenty_AreRead_TheRestAreLeft_AndLogged()
    {
        using var fixture = new LibraryFixture();
        var store = new DraftStore(fixture.Paths, fixture.Store, fixture.Log, () => fixture.Clock.Now);
        var written = new System.Collections.Generic.List<string>();
        for (var i = 0; i < DraftStore.MaxDraftsRead + 3; i++)
        {
            fixture.Clock.Tick();
            var document = PlateFactory.Create(PlateStartingLayout.Blank, Guid.NewGuid(), $"Plate {i}", fixture.Clock.Now);
            written.Add((await store.WriteAsync(store.Create(new ProfileService.OpenDocumentCopy(document, document, 0, document.UpdatedAtUtc), DraftEditor.None, "test")))!);
        }

        var listing = await store.ReadNewestAsync();

        Assert.Equal(3, listing.Unread);
        Assert.Equal(written.AsEnumerable().Reverse().Take(DraftStore.MaxDraftsRead), listing.Drafts.Select(d => d.Path));
        Assert.Equal(DraftStore.MaxDraftsRead + 3, KeptFiles.Drafts(fixture.Paths).Length);
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("W ", StringComparison.Ordinal) && m.Contains("the other 3 stay", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- newer and damaged drafts

    [Fact]
    public async Task ANewerDraft_IsNeverOffered_RestoredOrRewritten()
    {
        using var fixture = new LibraryFixture();
        WriteSavedVersionOfFixture(fixture);
        var newerEnvelope = FixtureText().Replace("\"Version\": 1,", "\"Version\": 2,", StringComparison.Ordinal);
        var newerDocument = FixtureText().Replace("\"Version\": 2,", "\"Version\": 99,", StringComparison.Ordinal);
        var first = KeptFiles.WriteDraftJson(fixture.Paths, FixturePlate, FixtureWritten, FixtureDraft, newerEnvelope);
        var second = KeptFiles.WriteDraftJson(fixture.Paths, FixturePlate, FixtureWritten.AddSeconds(1), Guid.NewGuid(), newerDocument);
        var game = await GameSession.StartAsync(fixture);
        var before = KeptFiles.Snapshot(fixture.Root);

        var listing = await game.Drafts.ReadNewestAsync();
        Assert.All(listing.Drafts, d => Assert.Equal(DraftReadStatus.NewerVersion, d.Status));
        Assert.Empty(await game.LoadKeptChangesAsync());

        Assert.False(game.Offer.HasCurrent);
        Assert.Null(game.Profiles.CurrentProfile);
        Assert.Equal(before, KeptFiles.Snapshot(fixture.Root));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task ADamagedDraft_IsLeftInPlace_WhileTheOthersAreStillOffered()
    {
        using var fixture = new LibraryFixture();
        var game = await GameSession.StartAsync(fixture);
        game.Open(await game.CreatePlateAsync(name: "Good"));
        game.Edit();
        await game.UnloadAsync();
        var good = Assert.Single(KeptFiles.Drafts(fixture.Paths));

        fixture.Clock.Tick();
        var damaged = KeptFiles.WriteDraftJson(fixture.Paths, Guid.NewGuid(), fixture.Clock.Now, Guid.NewGuid(), "{ \"Version\": 1, \"Document\": ");
        var mismatched = KeptFiles.WriteDraftJson(fixture.Paths, Guid.NewGuid(), fixture.Clock.Now, Guid.NewGuid(), File.ReadAllText(good));
        var damagedBytes = File.ReadAllBytes(damaged);

        var next = await GameSession.StartAsync(fixture);
        var found = Assert.Single(await next.LoadKeptChangesAsync());

        Assert.Equal(good, found.Path);
        Assert.Equal(damagedBytes, File.ReadAllBytes(damaged));
        Assert.True(File.Exists(mismatched));
        Assert.True(next.Offer.HasCurrent);
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("damaged and left as they are", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- unknown data

    [Fact]
    public async Task UnknownData_SurvivesRestore_FromTheSavedFile()
    {
        using var fixture = new LibraryFixture();
        WriteSavedVersionOfFixture(fixture);
        KeptFiles.WriteDraftJson(fixture.Paths, FixturePlate, FixtureWritten, FixtureDraft, FixtureText());
        var game = await GameSession.StartAsync(fixture);
        await game.LoadKeptChangesAsync();

        game.Offer.Choose();
        Assert.True(await game.Session.SaveProfileAsync());

        var saved = fixture.ReadPlateJson(FixturePlate);
        Assert.Contains("\"SavedOnlyField\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"FutureField\"", saved, StringComparison.Ordinal);
        Assert.Contains("hologram", saved, StringComparison.Ordinal);
        Assert.Contains("Kept in the draft", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownData_SurvivesRestoreAsNewPlate_FromTheDraft()
    {
        using var fixture = new LibraryFixture();
        KeptFiles.WriteDraftJson(fixture.Paths, FixturePlate, FixtureWritten, FixtureDraft, FixtureText());
        var game = await GameSession.StartAsync(fixture);
        Assert.Equal(KeptChangesChoice.Deleted, Assert.Single(await game.LoadKeptChangesAsync()).Choice);

        game.Offer.Choose();
        await game.SettleAsync();

        var created = Assert.Single(game.Library.GetOrderedPlates());
        Assert.NotEqual(FixturePlate, created.PlateId);
        var json = fixture.ReadPlateJson(created.PlateId);
        Assert.Contains("\"FutureField\"", json, StringComparison.Ordinal);
        Assert.Contains("hologram", json, StringComparison.Ordinal);
        Assert.Contains("Kept in the draft", json, StringComparison.Ordinal);
        Assert.Equal(created.PlateId, game.Profiles.OpenPlateId);
        Assert.False(game.Session.IsDirty);
    }

    /// <summary>
    /// The saved version the pinned draft started from: its document without the two elements the
    /// draft added, at the same revision and modified time, with a field only the saved file has.
    /// </summary>
    private static void WriteSavedVersionOfFixture(LibraryFixture fixture)
    {
        var document = JsonNode.Parse(FixtureText())!.AsObject()["Document"]!.AsObject();
        var saved = (JsonObject)document.DeepClone();
        saved["Elements"] = new JsonArray(saved["Elements"]!.AsArray().Where(e => e!["elementType"]!.GetValue<string>() == "hologram").Select(e => e!.DeepClone()).ToArray());
        saved["SavedOnlyField"] = "kept by the Plate";
        fixture.WritePlateJson(FixturePlate, saved.ToJsonString(JsonOptions.Default));
    }
}
