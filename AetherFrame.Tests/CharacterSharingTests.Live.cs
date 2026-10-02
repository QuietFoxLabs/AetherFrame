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
    public async Task ArrivingPublishesNothing_ButASaveOfTheActivePlateDoes()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        using var live = new LiveHarness(harness);
        var plate = live.Save();
        live.Active = plate.ProfileId;

        live.Frames(5);
        Assert.Empty(harness.Server.Publishes);

        live.Publisher.PlateSaved(plate.ProfileId);
        await live.Until(() => harness.Server.Publishes.Count == 1);
        Assert.Equal(SharingNoticeKind.Published, harness.Sharing.View.Notice!.Kind);
        Assert.Equal(plate.ProfileId, harness.Sharing.View.Find(Aria)!.PublishedPlate);

        live.Publisher.PlateSaved(plate.ProfileId);
        await live.Until(() => harness.Server.Publishes.Count == 2);
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
        await live.Until(() => harness.Server.Publishes.Count == 1);

        harness.Sharing.TryPause(Aria);
        live.Publisher.PlateSaved(plate.ProfileId);
        live.Publisher.Retry();
        live.Frames(20);
        Assert.Single(harness.Server.Publishes);

        harness.Sharing.TryResume(Aria);
        await live.Until(() => harness.Server.Publishes.Count == 2);
    }

    [Fact]
    public async Task MakingAnotherPlateActive_SharesItAtOnce()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        using var live = new LiveHarness(harness);
        var first = live.Save();
        live.Active = first.ProfileId;
        live.Frames(2);
        live.Publisher.PlateSaved(first.ProfileId);
        await live.Until(() => harness.Server.Publishes.Count == 1);

        // A Plate this character never shared, made Active: no save, no question.
        var second = live.Save(text: "Another Plate");
        live.Active = second.ProfileId;
        await live.Until(() => harness.Server.Publishes.Count == 2);
        Assert.Equal(second.ProfileId, Assert.Single(harness.Index(entry).Entries).PlateId);
        Assert.Equal(second.ProfileId, harness.Sharing.View.Find(Aria)!.PublishedPlate);
    }

    [Fact]
    public async Task MakingAPlateActive_ForACharacterThatDoesntShare_SendsNothing()
    {
        using var harness = new SharingHarness();
        using var live = new LiveHarness(harness);
        var first = live.Save();
        live.Active = first.ProfileId;
        live.Frames(2);

        var second = live.Save(text: "Another Plate");
        live.Active = second.ProfileId;
        live.Publisher.PlateSaved(second.ProfileId);
        live.Publisher.Retry();
        live.Frames(10);
        await Task.Delay(50);
        live.Frames(10);

        // Nothing at all reached the server: no publish, no action, no status, no challenge.
        Assert.Empty(harness.Server.Publishes);
        Assert.Empty(harness.Server.Actions);
        Assert.Equal((0, 0), (harness.Server.StatusRequests, harness.Server.Challenges));
        Assert.Null(harness.Sharing.View.Publish);
        Assert.False(live.Publisher.View.Building);
    }

    [Fact]
    public async Task TryingAgain_SharesTheActivePlateAgain()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        using var live = new LiveHarness(harness);
        var plate = live.Save();
        live.Active = plate.ProfileId;
        live.Frames(2);

        // The server can't be reached: the signed revision waits on this PC.
        harness.Server.Unreachable = true;
        live.Publisher.PlateSaved(plate.ProfileId);
        await live.Until(() => harness.Sharing.View.Publish is { Step: PublishStep.Ended });
        Assert.Equal(SharingNoticeKind.PublishWaiting, harness.Sharing.View.Publish!.Outcome!.Kind);

        // Trying again builds the Active Plate anew, as a save would, and its revision takes the
        // waiting one's place.
        harness.Server.Unreachable = false;
        live.Publisher.Retry();
        await live.Until(() => harness.Server.Publishes.Count == 1);
        Assert.Equal(SharingNoticeKind.Published, harness.Sharing.View.Publish!.Outcome!.Kind);
        var entry = Assert.Single(harness.Index(harness.Sharing.View.Find(Aria)!).Entries);
        Assert.Equal((PublicationState.Published, true), (entry.State, entry.PendingEntry.IsNone));
    }

    [Fact]
    public async Task ASendUnderWay_GivesWayToANewerSave_WhichIsSentInstead()
    {
        // The service runs on another thread, as in the game, and the server holds each publish.
        using var harness = new SharingHarness(background: true);
        var entry = harness.Bound();
        var held = new TaskCompletionSource();
        harness.Server.PublishGate = held.Task;
        using var live = new LiveHarness(harness);
        var plate = live.Save(text: "Older words");
        live.Active = plate.ProfileId;
        live.Frames(2);

        live.Publisher.PlateSaved(plate.ProfileId);
        await live.Until(() => harness.Sharing.View.Publish is { Step: PublishStep.Sending });
        var older = harness.Sharing.View.Publish!.Share;

        // Saved again while the first is being sent: once the newer candidate is ready, the older
        // send stops, and the newer one is signed and sent in its place.
        live.Save(text: "Newer words", id: plate.ProfileId);
        live.Publisher.PlateSaved(plate.ProfileId);

        // Ready while the older one is still being sent: the live publisher says it waits.
        await live.Until(() => live.Publisher.View is { Building: true, Waiting: true });
        Assert.Equal(Aria, live.Publisher.View.ContentId);
        await live.Until(() => harness.Sharing.View.Publish is { Step: PublishStep.Sending } newer && newer.Share != older);
        held.SetResult();
        await live.Until(() => harness.Sharing.View.Publish is { Step: PublishStep.Ended });
        harness.WaitIdle();

        var published = Assert.Single(harness.Server.Publishes);
        Assert.Equal("Newer words", TextOf(published));
        Assert.Equal(SharingNoticeKind.Published, harness.Sharing.View.Notice!.Kind);
        var recorded = Assert.Single(harness.Index(entry).Entries);
        Assert.Equal((PublicationState.Published, true, published.Snapshot.RevisionId), (recorded.State, recorded.PendingEntry.IsNone, recorded.LatestRevision));
        Assert.Empty(harness.Publications.ListOutbox(entry.Slot).Entries);
        Assert.Contains("Sharing: the send gave way to a newer build of the Active Plate.", harness.Log);
    }

    [Fact]
    public async Task AnActivePlateNotSharedYet_IsOfferedOnArriving_AndSharedOnlyWhenAsked()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        using var live = new LiveHarness(harness);
        var plate = live.Save();
        live.Active = plate.ProfileId;

        // Arriving publishes nothing: the Sharing window offers Share it now instead.
        live.Frames(5);
        Assert.Empty(harness.Server.Publishes);
        Assert.True(LivePublisher.OffersShareNow(harness.Sharing.View, live.Publisher.View, harness.Sharing.View.Find(Aria)!, live.Active));

        live.Publisher.Retry();
        live.Frames(1);
        Assert.True(live.Publisher.View.Building);
        Assert.False(LivePublisher.OffersShareNow(harness.Sharing.View, live.Publisher.View, entry, live.Active));
        await live.Until(() => harness.Server.Publishes.Count == 1);
        Assert.False(LivePublisher.OffersShareNow(harness.Sharing.View, live.Publisher.View, harness.Sharing.View.Find(Aria)!, live.Active));
    }

    [Fact]
    public void ShareItNow_IsOfferedOnlyForASharingCharactersActivePlateNotSharedYet_WithNothingUnderWay()
    {
        using var harness = new SharingHarness();
        var entry = harness.Bound();
        var plate = Guid.NewGuid();
        var view = harness.Sharing.View;
        Assert.True(LivePublisher.OffersShareNow(view, LiveView.Idle, entry, plate));

        // No Active Plate, or the one the server shows.
        Assert.False(LivePublisher.OffersShareNow(view, LiveView.Idle, entry, null));
        Assert.False(LivePublisher.OffersShareNow(view, LiveView.Idle, entry with { PublishedPlate = plate }, plate));

        // Paused, or another key being checked.
        Assert.False(LivePublisher.OffersShareNow(view, LiveView.Idle, entry with { Stage = SharingStage.Paused }, plate));

        // Something under way for it: a build, or a publish; another character's doesn't count.
        var building = new LiveView(Aria, true, Array.Empty<PlateSnapshotProblem>(), ShareCheckFailure.None, Share: 1);
        Assert.False(LivePublisher.OffersShareNow(view, building, entry, plate));
        Assert.True(LivePublisher.OffersShareNow(view, building with { ContentId = Bram }, entry, plate));
        Assert.False(LivePublisher.OffersShareNow(view.With(publish: new PublishStatus(Aria, 2, PublishStep.Sending)), LiveView.Idle, entry, plate));
        Assert.True(LivePublisher.OffersShareNow(view.With(publish: new PublishStatus(Bram, 2, PublishStep.Sending)), LiveView.Idle, entry, plate));
        Assert.True(LivePublisher.OffersShareNow(view.With(publish: new PublishStatus(Aria, 2, PublishStep.Ended)), LiveView.Idle, entry, plate));
    }

    [Fact]
    public async Task ASendUnderWay_StopsWhenItsPlateIsNoLongerActive_ButNotOnALogout()
    {
        using var harness = new SharingHarness(background: true);
        var entry = harness.Bound();
        var held = new TaskCompletionSource();
        harness.Server.PublishGate = held.Task;
        using var live = new LiveHarness(harness);
        var plate = live.Save();
        live.Active = plate.ProfileId;
        live.Frames(2);

        // The Active Plate is unset while it is being sent.
        live.Publisher.PlateSaved(plate.ProfileId);
        await live.Until(() => harness.Sharing.View.Publish is { Step: PublishStep.Sending });
        live.Active = null;
        await live.Until(() => harness.Sharing.View.Publish is { Step: PublishStep.Ended });
        harness.WaitIdle();
        Assert.Equal(SharingNoticeKind.PublishWithdrawn, harness.Sharing.View.Notice!.Kind);
        Assert.Empty(harness.Server.Publishes);
        Assert.Empty(harness.Publications.ListOutbox(entry.Slot).Entries);

        // Made Active again, then the character logs out while it is sent: the send goes on.
        live.Active = plate.ProfileId;
        await live.Until(() => harness.Sharing.View.Publish is { Step: PublishStep.Sending });
        live.LoggedIn = null;
        live.Frames(5);
        held.SetResult();
        await live.Until(() => harness.Sharing.View.Publish is { Step: PublishStep.Ended });
        harness.WaitIdle();
        Assert.Single(harness.Server.Publishes);
        Assert.Equal(SharingNoticeKind.Published, harness.Sharing.View.Notice!.Kind);
        Assert.Equal(plate.ProfileId, harness.Sharing.View.Find(Aria)!.PublishedPlate);
    }

    [Fact]
    public async Task ASendUnderWay_StopsWhenTheNewActivePlateCantBeShared()
    {
        using var harness = new SharingHarness(background: true);
        harness.Bound();
        var held = new TaskCompletionSource();
        harness.Server.PublishGate = held.Task;
        using var live = new LiveHarness(harness);
        var plate = live.Save();
        live.Active = plate.ProfileId;
        live.Frames(2);
        live.Publisher.PlateSaved(plate.ProfileId);
        await live.Until(() => harness.Sharing.View.Publish is { Step: PublishStep.Sending });

        try
        {
            var bad = live.Save(text: "Bad" + (char)0 + "text");
            live.Active = bad.ProfileId;
            await live.Until(() => live.Publisher.View.Problems.Count > 0 && harness.Sharing.View.Publish is { Step: PublishStep.Ended });
            harness.WaitIdle();
            Assert.Equal(SharingNoticeKind.PublishWithdrawn, harness.Sharing.View.Notice!.Kind);
            Assert.Empty(harness.Server.Publishes);
            Assert.Null(harness.Sharing.View.Find(Aria)!.PublishedPlate);
        }
        finally
        {
            held.TrySetResult();
        }
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
