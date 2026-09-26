using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AetherFrame.Domain.Components;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Packages;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// A profile.json well under every byte, depth and element limit can still be made of millions of
/// tiny JSON values; every validation stage walks or copies the whole tree, so such a package must
/// be refused cheaply — by the value-count limit, in the forward pass before any tree is built —
/// while every Plate an editor could make stays far inside that limit.
/// </summary>
public class JsonValueCountTests
{
    /// <summary>Every JSON value in <paramref name="utf8Json"/>, counted the way the limit counts them.</summary>
    internal static int CountValues(byte[] utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);
        var values = 0;
        while (reader.Read())
        {
            if (reader.TokenType is not (JsonTokenType.EndObject or JsonTokenType.EndArray or JsonTokenType.PropertyName))
            {
                values++;
            }
        }

        return values;
    }

    /// <summary>A valid Blank Plate package plus the tools to append an unknown top-level property to its profile.</summary>
    private sealed class Setup : IDisposable
    {
        private Setup(PackageFixture fixture, PlateLibraryService library, PlatePackageService packages, string validPath)
        {
            Fixture = fixture;
            Library = library;
            Packages = packages;
            ValidPath = validPath;
        }

        internal PackageFixture Fixture { get; }

        internal PlateLibraryService Library { get; }

        internal PlatePackageService Packages { get; }

        internal string ValidPath { get; }

        internal byte[] ValidProfile => PackageFiles.Entry(PackageFiles.Read(ValidPath), PackagePaths.ProfilePath).Bytes;

        internal static async Task<Setup> CreateAsync()
        {
            var fixture = new PackageFixture();
            var (library, packages) = await fixture.LoadAsync();
            var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Small");
            var document = library.OpenDocumentForEditing(created.PlateId);
            document.Elements.Add(new TextProfileElement { Text = "hello", Position = new Vector2(100, 10), Size = new Vector2(200, 40), ZIndex = 1 });
            await library.SavePlateDocumentAsync(document);
            return new Setup(fixture, library, packages, fixture.Export(packages, created.PlateId, "valid.aetherframe"));
        }

        /// <summary>The valid package with <c>, "Junk": &lt;json&gt;</c> spliced into its profile (resealed, so only the JSON differs).</summary>
        internal string WithJunk(string json, string fileName)
        {
            return PackageFiles.Rewrite(ValidPath, entries =>
            {
                var profile = PackageFiles.Entry(entries, PackagePaths.ProfilePath);
                var head = Encoding.UTF8.GetString(profile.Bytes).TrimEnd().TrimEnd('}');
                profile.Bytes = Encoding.UTF8.GetBytes(head + ",\"Junk\":" + json + "}");
            }, reseal: true, fileName: fileName);
        }

        public void Dispose() => Fixture.Dispose();
    }

    private static string RepeatedArray(string element, int count)
    {
        var builder = new StringBuilder(count * (element.Length + 1) + 2);
        builder.Append('[');
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(element);
        }

        return builder.Append(']').ToString();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("{}")]
    public async Task ManyTinyValuesWithinByteLimit_IsRefusedCheaplyBeforeBuildingTheTree(string element)
    {
        using var setup = await Setup.CreateAsync();

        // As many values as fit inside the byte limit: far past the value limit either way.
        var budget = PackagePolicy.MaxProfileBytes - setup.ValidProfile.Length - 16;
        var count = budget / (element.Length + 1);
        Assert.True(count > 4 * PackagePolicy.MaxJsonValueCount);
        var path = setup.WithJunk(RepeatedArray(element, count), "junk.aetherframe");
        Assert.True(PackageFiles.Entry(PackageFiles.Read(path), PackagePaths.ProfilePath).Bytes.Length <= PackagePolicy.MaxProfileBytes);
        var before = setup.Fixture.SnapshotInstallation();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        var staged = setup.Packages.Inspect(path);
        clock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        try
        {
            Assert.False(staged.CanImport, $"a {count:N0}-value profile was importable: {clock.ElapsedMilliseconds} ms, {allocated >> 20} MiB allocated");
            Assert.Equal(PackageCompatibility.Invalid, staged.Compatibility);
            var error = Assert.Single(staged.Diagnostics.Errors);
            Assert.Equal(PackageErrorCode.PackageTooLarge, error.Code);
            Assert.Contains($"more than {PackagePolicy.MaxJsonValueCount} JSON values", error.Detail);
            Assert.Null(staged.PreparedProfile);
            Assert.Null(staged.PreviewDocument);
            Assert.True(allocated < 128L * 1024 * 1024, $"validating the profile allocated {allocated >> 20} MiB");
            Assert.True(clock.ElapsedMilliseconds < 2000, $"validating the profile took {clock.ElapsedMilliseconds} ms");
            Assert.False((await setup.Packages.ImportAsync(staged)).Succeeded);
        }
        finally
        {
            staged.Dispose();
        }

        Assert.Equal(before, setup.Fixture.SnapshotInstallation());
        Assert.True(setup.Fixture.StagingIsEmpty);
    }

    [Fact]
    public async Task ValueCount_IsRefusedExactlyPastTheLimit()
    {
        using var setup = await Setup.CreateAsync();
        var baseline = CountValues(setup.ValidProfile);

        // The array itself is one value; its zeros are the rest.
        var atTheLimit = setup.WithJunk(RepeatedArray("0", PackagePolicy.MaxJsonValueCount - baseline - 1), "at-limit.aetherframe");
        Assert.Equal(PackagePolicy.MaxJsonValueCount, CountValues(PackageFiles.Entry(PackageFiles.Read(atTheLimit), PackagePaths.ProfilePath).Bytes));
        using (var staged = setup.Packages.Inspect(atTheLimit))
        {
            Assert.True(staged.CanImport, staged.DescribeForLog());
        }

        var pastTheLimit = setup.WithJunk(RepeatedArray("0", PackagePolicy.MaxJsonValueCount - baseline), "past-limit.aetherframe");
        using (var staged = setup.Packages.Inspect(pastTheLimit))
        {
            Assert.False(staged.CanImport);
            Assert.Contains(staged.Diagnostics.Errors, e => e.Code == PackageErrorCode.PackageTooLarge);
        }
    }

    [Fact]
    public async Task PropertyNameLongerThanTheLimit_IsRefused_AndOneAtTheLimitIsNot()
    {
        using var setup = await Setup.CreateAsync();

        var atTheLimit = setup.WithJunk("{\"" + new string('n', PackagePolicy.MaxJsonPropertyNameLength) + "\":1}", "long-name.aetherframe");
        using (var staged = setup.Packages.Inspect(atTheLimit))
        {
            Assert.True(staged.CanImport, staged.DescribeForLog());
        }

        var pastTheLimit = setup.WithJunk("{\"" + new string('n', PackagePolicy.MaxJsonPropertyNameLength + 1) + "\":1}", "longer-name.aetherframe");
        using (var staged = setup.Packages.Inspect(pastTheLimit))
        {
            Assert.False(staged.CanImport);
            var error = Assert.Single(staged.Diagnostics.Errors);
            Assert.Equal(PackageErrorCode.PackageTooLarge, error.Code);
            Assert.Contains("property name", error.Detail);
            Assert.Null(staged.PreparedProfile);
        }

        Assert.True(setup.Fixture.StagingIsEmpty);
    }

    [Fact]
    public async Task OversizedManifest_IsRefusedByTheSamePass()
    {
        using var setup = await Setup.CreateAsync();
        var path = PackageFiles.Rewrite(setup.ValidPath, entries => PackageFiles.EditManifest(entries, m => m[new string('n', PackagePolicy.MaxJsonPropertyNameLength + 1)] = 1), reseal: false);

        using var staged = setup.Packages.Inspect(path);

        Assert.False(staged.CanImport);
        Assert.Contains(staged.Diagnostics.Errors, e => e.Code == PackageErrorCode.PackageTooLarge);
        Assert.Null(staged.Manifest);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void TryParseObject_RefusesAnOversizedDocumentWithoutBuildingATree(string element)
    {
        // 8 MiB of tiny values: the refusal must cost a scan, not a tree.
        var count = PackagePolicy.MaxProfileBytes / (element.Length + 1);
        var utf8 = Encoding.UTF8.GetBytes("{\"a\":" + RepeatedArray(element, count) + "}");
        Assert.True(CountValues(utf8) > PackagePolicy.MaxJsonValueCount);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var accepted = PackageJson.TryParseObject(utf8, out var result, out var problem);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.False(accepted);
        Assert.Null(result);
        Assert.Equal(PackageJsonProblemKind.TooLarge, problem!.Kind);
        Assert.True(allocated < utf8.Length / 8, $"refusing a {utf8.Length >> 20} MiB document allocated {allocated} bytes");
    }

    [Fact]
    public void TryParseObject_StillRefusesDuplicatesDepthAndMalformedInput()
    {
        Assert.False(PackageJson.TryParseObject("{\"a\":1,\"a\":2}"u8, out _, out var duplicate));
        Assert.Equal(PackageJsonProblemKind.Malformed, duplicate!.Kind);
        Assert.Contains("duplicate", duplicate.Detail);

        // The same name in different objects is fine.
        Assert.True(PackageJson.TryParseObject("{\"a\":{\"a\":1},\"b\":[{\"a\":2}]}"u8, out _, out _));

        var deep = Encoding.UTF8.GetBytes("{\"a\":" + new string('[', PackagePolicy.MaxJsonDepth) + new string(']', PackagePolicy.MaxJsonDepth) + "}");
        Assert.False(PackageJson.TryParseObject(deep, out _, out var tooDeep));
        Assert.Equal(PackageJsonProblemKind.Malformed, tooDeep!.Kind);

        Assert.False(PackageJson.TryParseObject("[1, 2]"u8, out _, out var array));
        Assert.Equal(PackageJsonProblemKind.Malformed, array!.Kind);
        Assert.False(PackageJson.TryParseObject("{\"a\":1} x"u8, out _, out var trailing));
        Assert.Equal(PackageJsonProblemKind.Malformed, trailing!.Kind);
        Assert.False(PackageJson.TryParseObject("{\"a\":1,}"u8, out _, out _));
        Assert.False(PackageJson.TryParseObject("{/* c */\"a\":1}"u8, out _, out _));
        Assert.True(PackageJson.TryParseObject([0xEF, 0xBB, 0xBF, .. "{\"a\":1}"u8], out _, out _));
    }

    [Fact]
    public async Task MaximalEditorMadePlate_ExportsAndReImports_FarInsideTheValueLimit()
    {
        using var fixture = new PackageFixture();
        var (library, packages) = await fixture.LoadAsync();
        var created = await library.CreatePlateAsync(PlateStartingLayout.Blank, null, "Maximal");
        var document = library.OpenDocumentForEditing(created.PlateId);

        // Every element slot, every text and style field set, and every Component slot filled.
        for (var i = document.Elements.Count; i < ProfileDocument.MaxElementCount; i++)
        {
            document.Elements.Add(new TextProfileElement
            {
                Name = new string('n', ProfileElement.MaxNameLength),
                Text = new string((char)('a' + i % 26), TextProfileElement.MaxTextLength),
                Prefix = new string('p', TextProfileElement.MaxAffixLength),
                Suffix = new string('s', TextProfileElement.MaxAffixLength),
                FontFamily = ProfileFontFamilies.AetherFrameSerif,
                FontSize = 24 + i % 40,
                Color = new Vector4(0.1f, 0.2f, 0.3f, 1f),
                Alignment = TextAlignment.Center,
                VerticalAlignment = TextVerticalAlignment.Middle,
                Wrap = false,
                Bold = true,
                Italic = true,
                Underline = true,
                Strikethrough = true,
                LetterSpacing = 1.5f,
                LineSpacing = 1.25f,
                AutoFitText = true,
                AutoFitMinimumSize = 10f,
                OutlineEnabled = true,
                OutlineColor = new Vector4(0.4f, 0.5f, 0.6f, 1f),
                OutlineThickness = 3f,
                OutlineOpacity = 0.9f,
                ShadowEnabled = true,
                ShadowColor = new Vector4(0.7f, 0.8f, 0.9f, 1f),
                ShadowOpacity = 0.5f,
                ShadowOffsetX = 4f,
                ShadowOffsetY = 5f,
                Position = new Vector2(10 + i, 20 + i),
                Size = new Vector2(400, 60),
                ZIndex = i,
                Role = ProfileElementRole.None,
            });
        }

        var definitions = BuiltInComponentCatalog.All.Where(d => !d.RequiresAsset).ToList();
        document.Components = [];
        for (var i = 0; i < PlateComponentLimits.MaxComponentCount; i++)
        {
            var definition = definitions[i % definitions.Count];
            document.Components.Add(new PlateComponent
            {
                Kind = definition.Kind,
                DefinitionId = definition.Id,
                Color = new Vector4(0.2f, 0.4f, 0.6f, 1f),
                Opacity = 0.8f,
                Offset = new Vector2(i, -i),
                Scale = 1.5f,
                RotationDegrees = 15f,
                LayerOrder = i - 16,
            });
        }

        await library.SavePlateDocumentAsync(document);
        var path = fixture.Export(packages, created.PlateId);

        var profile = PackageFiles.Entry(PackageFiles.Read(path), PackagePaths.ProfilePath).Bytes;
        var values = CountValues(profile);
        Assert.True(values < PackagePolicy.MaxJsonValueCount / 2, $"a maximal Plate is {values:N0} values; the limit is {PackagePolicy.MaxJsonValueCount:N0}");
        Assert.True(profile.Length <= PackagePolicy.MaxProfileBytes);

        using var staged = packages.Inspect(path);
        Assert.True(staged.CanImport, staged.DescribeForLog());
        var result = await packages.ImportAsync(staged);
        Assert.True(result.Succeeded, result.Error?.Message);

        var imported = library.OpenDocumentForEditing(result.PlateId);
        Assert.Equal(ProfileDocument.MaxElementCount, imported.Elements.Count);
        Assert.Equal(PlateComponentLimits.MaxComponentCount, imported.Components!.Count);
        Assert.All(imported.Elements.OfType<TextProfileElement>().Skip(1), t => Assert.Equal(TextProfileElement.MaxTextLength, t.Text.Length));
    }

    [Fact]
    public void ValueLimit_AllowsALargeUnknownPayloadOfARealisticShape()
    {
        // Forward compatibility: a newer build's data of an ordinary shape — a few thousand
        // objects of a dozen fields — is nowhere near the limit.
        var payload = new JsonArray();
        for (var i = 0; i < 2_000; i++)
        {
            payload.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["kind"] = "future",
                ["x"] = i,
                ["y"] = i * 2.5,
                ["visible"] = true,
                ["tags"] = new JsonArray("a", "b", "c"),
                ["style"] = new JsonObject { ["color"] = "#ffffff", ["weight"] = 3 },
            });
        }

        var utf8 = Encoding.UTF8.GetBytes(new JsonObject { ["Future"] = payload }.ToJsonString());

        Assert.True(PackageJson.TryParseObject(utf8, out var result, out _));
        Assert.Equal(2_000, result!["Future"]!.AsArray().Count);
        Assert.True(CountValues(utf8) < PackagePolicy.MaxJsonValueCount / 4);
    }
}
