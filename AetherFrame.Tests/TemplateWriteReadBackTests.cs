using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Domain.Templates;
using AetherFrame.Persistence;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A Template write is proven first to load again as a Ready Template through the loader's own
/// reader, as a Plate write is (see <see cref="WriteReadBackTests"/>): after Save as Template,
/// Rename or Duplicate, what the Library shows and uses is what the next startup reads, even when a
/// name held half of a surrogate pair, which the file can't carry as given. A write that wouldn't
/// load again is refused, with the same message as a Plate's, before any file is touched. A Template
/// holds its Plate's document one level deeper than the Plate's own file does, so a Plate whose
/// content (from a newer build, say) is nested to the JSON reader's depth limit is such a write when
/// it is saved as a Template.
///
/// <para>What these can't observe: the byte-level round trip the write path also makes
/// (<see cref="VersionedJson.RequireFaithfulReadBack"/>) never has anything to refuse here, since
/// the serializer's output is always ASCII (a lone surrogate is written escaped). That check is
/// covered at unit level by <see cref="StoredTextDecoderTests"/>.</para>
/// </summary>
public class TemplateWriteReadBackTests
{
    /// <summary>How many objects and arrays deep the Libraries' JSON reader reads (its default limit).</summary>
    private const int ReaderDepthLimit = 64;

    /// <summary><paramref name="levels"/> arrays, each inside the next, around a number.</summary>
    private static JsonNode Nested(int levels)
    {
        JsonNode node = JsonValue.Create(1);
        for (var i = 0; i < levels; i++)
        {
            node = new JsonArray(node);
        }

        return node;
    }

    /// <summary>
    /// A current Plate's file as a newer build could write it: holding content this build doesn't
    /// know (an element of a new type, or a new property) nested so that the whole file is
    /// <paramref name="depth"/> objects and arrays deep.
    /// </summary>
    private static string NestedPlateJson(Guid plateId, string carrier, int depth, DateTime now)
    {
        var raw = PlateDocuments.ToJson(PlateFactory.Create(PlateStartingLayout.Blank, plateId, "Nested", now));
        if (carrier == "element")
        {
            // The Plate, its Elements array and the element itself are the first three levels.
            raw[nameof(ProfileDocument.Elements)]!.AsArray().Add(new JsonObject
            {
                [ProfileElement.TypeDiscriminatorPropertyName] = "fromANewerBuild",
                ["Id"] = Guid.NewGuid().ToString(),
                ["Data"] = Nested(depth - 3),
            });
        }
        else
        {
            raw["FromANewerBuild"] = Nested(depth - 1);
        }

        return VersionedJson.Serialize(raw);
    }

    private static string DocumentJson(ProfileDocument? document) => PlateDocuments.ToJson(document!).ToJsonString();

    private static string[] TemplateFiles(PlateStoragePaths paths) =>
        Directory.Exists(paths.TemplatesDirectory) ? Directory.GetFiles(paths.TemplatesDirectory, "*", SearchOption.AllDirectories) : [];

    /// <summary>True when every surrogate in <paramref name="text"/> is half of a pair.</summary>
    private static bool IsWellFormed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<TemplateLibraryService> ReloadAsync(TemplateLibraryFixture fixture)
    {
        var reloaded = fixture.CreateService();
        await reloaded.InitializeAsync();
        return reloaded;
    }

    [Fact]
    public async Task EveryWrite_ShowsAndUsesWhatTheNextStartupReads()
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.AdventurePlateClassic, Characters.Alice, "\u00C6lfwyn's Plate \u2726");
        var savedId = await templates.SaveAsTemplateAsync(plate.PlateId, null);
        fixture.Clock.Tick();
        var renamedId = await templates.SaveAsTemplateAsync(plate.PlateId, "Spare");
        fixture.Clock.Tick();
        await templates.RenameTemplateAsync(renamedId, "\u00C6lfwyn \uFFFD \U0001F338 Template");
        fixture.Clock.Tick();
        var copyId = await templates.DuplicateTemplateAsync(renamedId);

        var reloaded = await ReloadAsync(fixture);

        foreach (var id in new[] { savedId, renamedId, copyId })
        {
            // UTF-8 without a byte order mark, with everything outside ASCII escaped.
            Assert.True(Ascii.IsValid(File.ReadAllBytes(fixture.Paths.GetTemplatePath(id))));
            Assert.Equal(templates.FindTemplate(id), reloaded.FindTemplate(id));
            Assert.Equal(DocumentJson(templates.GetSavedDocument(id)), DocumentJson(reloaded.GetSavedDocument(id)));

            // Use Template copies the saved text itself, not the document shown.
            var fromKept = await templates.InstantiateAsync(id, null);
            var fromFile = await reloaded.InstantiateAsync(id, null);
            JsonAssert.EqualExcept(
                fixture.PlateLibrary.GetSavedJsonForExport(fromKept.PlateId).Json,
                fixture.PlateLibrary.GetSavedJsonForExport(fromFile.PlateId).Json,
                nameof(ProfileDocument.ProfileId),
                nameof(ProfileDocument.Name));
        }

        Assert.Equal("\u00C6lfwyn \uFFFD \U0001F338 Template", reloaded.FindTemplate(renamedId)!.DisplayName);
        Assert.All(reloaded.GetOrderedTemplates().Where(t => !t.IsBuiltIn), t =>
        {
            Assert.Equal(TemplateStatus.Ready, t.Status);
            Assert.Null(t.Problem);
        });
    }

    [Theory]
    [InlineData("save")]
    [InlineData("rename")]
    [InlineData("duplicate")]
    public async Task NameHoldingHalfASurrogatePair_IsKeptExactlyAsTheFileHoldsIt(string write)
    {
        using var fixture = new TemplateLibraryFixture();
        var templates = await fixture.LoadAsync();
        var plate = await fixture.PlateLibrary.CreatePlateAsync(PlateStartingLayout.Blank, null, "Source");

        Guid templateId;
        if (write == "save")
        {
            templateId = await templates.SaveAsTemplateAsync(plate.PlateId, "Half \uD83C pair");
        }
        else if (write == "rename")
        {
            templateId = await templates.SaveAsTemplateAsync(plate.PlateId, "Whole");
            await templates.RenameTemplateAsync(templateId, "Half \uDF38 pair");
        }
        else
        {
            // The copy's name is shortened to fit, between the two halves of the emoji.
            var sourceName = new string('a', 58) + "\U0001F338bbbb";
            Assert.False(IsWellFormed(TemplateNaming.MakeCopyName(sourceName, [])));
            var sourceId = await templates.SaveAsTemplateAsync(plate.PlateId, sourceName);
            templateId = await templates.DuplicateTemplateAsync(sourceId);
        }

        var kept = templates.FindTemplate(templateId)!;
        var file = File.ReadAllBytes(fixture.Paths.GetTemplatePath(templateId));
        Assert.True(Ascii.IsValid(file));
        Assert.Equal(JsonNode.Parse(file)![nameof(PlateTemplate.Name)]!.GetValue<string>(), kept.DisplayName);
        Assert.True(IsWellFormed(kept.DisplayName));

        var reloaded = await ReloadAsync(fixture);
        Assert.Equal(kept, reloaded.FindTemplate(templateId));
        Assert.Equal(TemplateStatus.Ready, kept.Status);
        Assert.Null(kept.Problem);
        Assert.Equal(DocumentJson(templates.GetSavedDocument(templateId)), DocumentJson(reloaded.GetSavedDocument(templateId)));
    }

    [Fact]
    public async Task TemplateNestedPastTheReadersLimit_DoesNotLoad()
    {
        using var fixture = new TemplateLibraryFixture();
        var templateId = Guid.NewGuid();
        var document = NestedPlateJson(Guid.NewGuid(), "element", ReaderDepthLimit, fixture.Clock.Now);
        fixture.WriteTemplateJson(templateId, TemplateSamples.Envelope(templateId, "Nested", document));

        var templates = await fixture.LoadAsync();

        Assert.Equal(TemplateStatus.Unreadable, templates.FindTemplate(templateId)!.Status);
        Assert.Equal(TemplateLibraryService.DamagedTemplateProblem, templates.FindTemplate(templateId)!.Problem);
    }

    [Theory]
    [InlineData("element")]
    [InlineData("property")]
    public async Task SaveAsTemplate_OfAPlateJustWithinTheLimitOnceWrapped_IsWritten_AndLoadsReady(string carrier)
    {
        using var fixture = new TemplateLibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, NestedPlateJson(plateId, carrier, ReaderDepthLimit - 1, fixture.Clock.Now));
        var templates = await fixture.LoadAsync();

        var templateId = await templates.SaveAsTemplateAsync(plateId, "Nested");

        var reloaded = await ReloadAsync(fixture);
        Assert.Equal(TemplateStatus.Ready, reloaded.FindTemplate(templateId)!.Status);
        Assert.Equal(templates.FindTemplate(templateId), reloaded.FindTemplate(templateId));
        Assert.Equal(DocumentJson(templates.GetSavedDocument(templateId)), DocumentJson(reloaded.GetSavedDocument(templateId)));
    }

    [Theory]
    [InlineData("element")]
    [InlineData("property")]
    public async Task SaveAsTemplate_OfAPlateNestedToTheReadersLimit_IsRefusedAsUnloadable_BeforeAnythingIsWritten(string carrier)
    {
        using var fixture = new TemplateLibraryFixture();
        var plateId = Guid.NewGuid();
        fixture.WritePlateJson(plateId, NestedPlateJson(plateId, carrier, ReaderDepthLimit, fixture.Clock.Now));
        var templates = await fixture.LoadAsync();
        Assert.Equal(PlateStatus.Ready, fixture.PlateLibrary.FindPlate(plateId)!.Status);
        var plateFile = File.ReadAllBytes(fixture.Paths.GetPlatePath(plateId));
        var listed = templates.GetOrderedTemplates();
        var generation = templates.Generation;

        var refused = await Record.ExceptionAsync(() => templates.SaveAsTemplateAsync(plateId, "Nested"));

        Assert.NotNull(refused);
        Assert.Empty(TemplateFiles(fixture.Paths));
        Assert.Equal(listed, templates.GetOrderedTemplates());
        Assert.Equal(generation, templates.Generation);
        Assert.Equal(plateFile, File.ReadAllBytes(fixture.Paths.GetPlatePath(plateId)));
        Assert.Equal(PlateLibraryService.UnloadableWriteMessage, UserFacingError.Describe(refused, "The Template couldn't be saved."));
        Assert.Equal(PlateLibraryService.UnloadableWriteMessage, Assert.IsType<TemplateLibraryException>(refused).Message);
        Assert.Contains(fixture.Log.Messages, m => m.StartsWith("E ", StringComparison.Ordinal) && m.Contains("did not", StringComparison.Ordinal) && m.Contains("Template", StringComparison.Ordinal));
    }
}
