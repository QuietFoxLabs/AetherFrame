using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Domain.Profiles;
using AetherFrame.Services.Network.Publishing;
using AetherFrame.Services.Network.Sharing;
using AetherFrame.Services.Plates;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// Publishing the Active Plate live (N2-9c): the live publisher, driven a frame at a time as the
/// plugin drives it, over a real share check with the image pipeline faked, and the same server
/// answered in memory.
/// </summary>
public partial class CharacterSharingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ArrivingPublishesNothing_ButASaveOfTheActivePlateDoes_AfterItsFirstShowing()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        using var live = new LiveHarness(harness);
        var plate = live.Save();
        live.Active = plate.ProfileId;

        live.Frames(5);
        Assert.Null(harness.Sharing.View.Consent);
        Assert.Empty(harness.Server.Publishes);

        live.Publisher.PlateSaved(plate.ProfileId);
        await live.Until(() => harness.Sharing.View.Consent is not null);
        var consent = harness.Sharing.View.Consent!;
        Assert.Equal(plate.ProfileId, consent.Candidate.PlateId);
        Assert.Empty(harness.Server.Publishes);

        harness.Sharing.TryPublish(Aria, consent.Candidate, approved: true, live.Active);
        Assert.Single(harness.Server.Publishes);

        live.Publisher.PlateSaved(plate.ProfileId);
        await live.Until(() => harness.Server.Publishes.Count == 2);
        Assert.Null(harness.Sharing.View.Consent);
        Assert.Equal(SharingNoticeKind.Published, harness.Sharing.View.Notice!.Kind);
    }

    [Fact]
    public async Task APlateThatCantBeSharedAsItIs_SaysWhy_AndSendsNothing()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        using var live = new LiveHarness(harness);
        var plate = live.Save(text: "Bad" + (char)0 + "text");
        live.Active = plate.ProfileId;
        live.Frames(2);

        live.Publisher.PlateSaved(plate.ProfileId);
        await live.Until(() => live.Publisher.View.Problems.Count > 0);
        Assert.Equal(Aria, live.Publisher.View.ContentId);
        Assert.Null(harness.Sharing.View.Consent);
        Assert.Empty(harness.Server.Publishes);
    }

    [Fact]
    public async Task APausedCharacter_SharesNothingOnSave_AndResumingSharesItsActivePlateAgain()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        using var live = new LiveHarness(harness);
        var plate = live.Save();
        live.Active = plate.ProfileId;
        live.Frames(2);
        live.Publisher.PlateSaved(plate.ProfileId);
        await live.Until(() => harness.Sharing.View.Consent is not null);
        harness.Sharing.TryPublish(Aria, harness.Sharing.View.Consent!.Candidate, approved: true, live.Active);

        harness.Sharing.TryPause(Aria);
        live.Publisher.PlateSaved(plate.ProfileId);
        live.Frames(20);
        Assert.Single(harness.Server.Publishes);

        harness.Sharing.TryResume(Aria);
        await live.Until(() => harness.Server.Publishes.Count == 2);
        Assert.Null(harness.Sharing.View.Consent);
    }

    [Fact]
    public async Task AnotherActivePlate_IsShownBeforeItIsShared()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        using var live = new LiveHarness(harness);
        var first = live.Save();
        live.Active = first.ProfileId;
        live.Frames(2);
        live.Publisher.PlateSaved(first.ProfileId);
        await live.Until(() => harness.Sharing.View.Consent is not null);
        harness.Sharing.TryPublish(Aria, harness.Sharing.View.Consent!.Candidate, approved: true, live.Active);

        var second = live.Save(text: "Another Plate");
        live.Active = second.ProfileId;
        await live.Until(() => harness.Sharing.View.Consent is { } shown && shown.Candidate.PlateId == second.ProfileId);
        Assert.Single(harness.Server.Publishes);
    }

    [Fact]
    public void AtLogin_ARenamedCharacterAsksForARereadOnce()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        using var live = new LiveHarness(harness) { Name = "Aria Moonfall" };
        harness.Server.Answers["/v1/lodestone/reread"] = _ => (System.Net.HttpStatusCode.OK, "{\"name\":\"Aria Moonfall\",\"world\":\"Gilgamesh\"}");

        live.Frames(5);
        Assert.Single(harness.Server.Actions, action => action.Path == "/v1/lodestone/reread");
        Assert.Equal("Aria Moonfall", harness.Sharing.View.Find(Aria)!.Name);
    }

    /// <summary>A live publisher over the harness's service, with a share check whose images are prepared in memory, and a character logged in as Aria.</summary>
    private sealed class LiveHarness : IDisposable
    {
        private readonly Dictionary<Guid, ProfileDocument> saved = new();

        internal LiveHarness(SharingHarness harness)
        {
            var check = new ShareCheck(new ShareCheckSeams
            {
                OpenSavedPlate = id => saved.TryGetValue(id, out var plate) ? plate : null,
                Prewarm = _ => { },
                Measurements = new FixedMeasurements(),
                SelfTest = () => Task.FromResult(true),
                Prepare = PrepareInMemory,
                BeginOperation = () => new Nothing(),
                Log = harness.Log.Add,
            });
            Publisher = new LivePublisher(harness.Sharing, check, () => LoggedIn is { } contentId ? new CharacterContext(contentId, Name, "Gilgamesh") : null, contentId => contentId == Aria ? Active : null, () => LibraryLoaded);
        }

        internal LivePublisher Publisher { get; }

        internal Guid? Active { get; set; }

        /// <summary>The character logged in, by Content ID; none for null.</summary>
        internal ulong? LoggedIn { get; set; } = Aria;

        /// <summary>Whether the Library has loaded, which the publisher waits for before arriving.</summary>
        internal bool LibraryLoaded { get; set; } = true;

        internal string Name { get; init; } = "Aria Starfall";

        internal ProfileDocument Save(string text = "Shared words", Guid? id = null)
        {
            var plate = PlateFactory.Create(PlateStartingLayout.Blank, id ?? Guid.NewGuid(), "Shared", PublicationCandidates.Now);
            plate.Elements.Add(new TextProfileElement { Text = text, Position = new Vector2(20f, 20f), Size = new Vector2(300f, 60f), ZIndex = 0 });
            plate.Elements.Add(new ImageProfileElement { AssetId = Guid.NewGuid(), Position = new Vector2(40f, 120f), Size = new Vector2(64f, 32f), ZIndex = 1 });
            saved[plate.ProfileId] = plate;
            return plate;
        }

        internal void Frames(int count)
        {
            for (var frame = 0; frame < count; frame++)
            {
                Publisher.OnFrame();
            }
        }

        /// <summary>Runs frames until <paramref name="condition"/> holds, as the game's frames would.</summary>
        internal async Task Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "timed out");
                Publisher.OnFrame();
                await Task.Delay(5);
            }
        }

        public void Dispose() => Publisher.Dispose();

        private static Task<IReadOnlyDictionary<ImageRequirement, ImagePreparation>> PrepareInMemory(IReadOnlyList<ImageRequirement> requirements, CancellationToken cancellation)
        {
            var prepared = new Dictionary<ImageRequirement, ImagePreparation>();
            foreach (var requirement in requirements)
            {
                var png = PreparedPngs.Png(requirement.Window.Width, requirement.Window.Height);
                prepared[requirement] = ImagePreparation.Prepared(PreparedPngs.Prepared(png, requirement.Window.Width, requirement.Window.Height));
            }

            return Task.FromResult<IReadOnlyDictionary<ImageRequirement, ImagePreparation>>(prepared);
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
