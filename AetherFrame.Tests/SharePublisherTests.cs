using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Personas;
using AetherFrame.Services.Network.Publishing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// NETWORK2's publisher (N2-6c's second part): signing and listing run as persona-session
/// operations, one at a time; a signing lists the persona's publications again in the same
/// operation; and the log gets result and exception kinds, never an id. Signed with throwaway keys.
/// </summary>
public sealed class SharePublisherTests : IDisposable
{
    private readonly PublicationFixture fixture = new();
    private readonly List<string> log = new();
    private readonly List<string> ran = new();
    private bool sessionBusy;

    public void Dispose() => fixture.Dispose();

    [Fact]
    public void Signing_KeepsTheRevision_AndListsThePersonasPublicationsAgain()
    {
        var publisher = NewPublisher();
        var candidate = PublicationCandidates.Simple();

        Assert.True(publisher.TrySign(candidate, fixture.Persona.Slot, fixture.Persona.PublicKey));

        var view = publisher.View;
        Assert.False(view.Busy);
        Assert.Equal(PublishResult.Stored, view.LastOutcome!.Result);
        Assert.Equal(fixture.Persona.Slot, view.Listed);
        var listed = Assert.Single(view.Publications!.Entries);
        Assert.Equal((candidate.PlateId, OutboxState.Waiting), (listed.Entry.PlateId, listed.Outbox));
        Assert.Equal(new[] { "publish" }, ran);

        // The log says what it came to, and names no id.
        Assert.Contains("Sharing: signing came to Stored.", log);
        foreach (var line in log)
        {
            Assert.DoesNotContain(view.LastOutcome.Profile.ToString(), line, StringComparison.Ordinal);
            Assert.DoesNotContain(view.LastOutcome.Revision.ToString(), line, StringComparison.Ordinal);
            Assert.DoesNotContain(candidate.PlateId.ToString(), line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ARefusal_IsReported_AndLoggedByKind()
    {
        using var unacknowledged = new PublicationFixture(acknowledged: false);
        var publisher = new SharePublisher(RunNow(unacknowledged.Personas), unacknowledged.Files, () => unacknowledged.Now, log.Add);

        Assert.True(publisher.TrySign(PublicationCandidates.Simple(), unacknowledged.Persona.Slot, unacknowledged.Persona.PublicKey));

        Assert.Equal(PublishResult.NotAcknowledged, publisher.View.LastOutcome!.Result);
        Assert.Empty(publisher.View.Publications!.Entries);
        Assert.Contains("Sharing: signing came to NotAcknowledged.", log);
    }

    [Fact]
    public void ABusySession_StartsNothing_AndLeavesThePublisherIdle()
    {
        var publisher = NewPublisher();
        sessionBusy = true;

        Assert.False(publisher.TrySign(PublicationCandidates.Simple(), fixture.Persona.Slot, fixture.Persona.PublicKey));
        Assert.False(publisher.TryList(fixture.Persona.Slot, fixture.Persona.PublicKey));

        Assert.False(publisher.View.Busy);
        Assert.Null(publisher.View.LastOutcome);
        Assert.Empty(ran);
        Assert.Empty(fixture.AllFiles());
    }

    [Fact]
    public async Task OneOperationAtATime_WhileItRuns_NothingElseStarts()
    {
        using var release = new ManualResetEventSlim(false);
        Task? running = null;
        var publisher = new SharePublisher(
            (name, work) =>
            {
                running = Task.Run(() =>
                {
                    release.Wait(TimeSpan.FromSeconds(10));
                    work(fixture.Personas);
                });
                return true;
            },
            fixture.Files,
            () => fixture.Now,
            log.Add);

        Assert.True(publisher.TryList(fixture.Persona.Slot, fixture.Persona.PublicKey));
        Assert.True(publisher.View.Busy);
        Assert.False(publisher.TrySign(PublicationCandidates.Simple(), fixture.Persona.Slot, fixture.Persona.PublicKey));

        release.Set();
        await running!;
        Assert.False(publisher.View.Busy);
        Assert.Equal(fixture.Persona.Slot, publisher.View.Listed);
        Assert.Empty(publisher.View.Publications!.Entries);
    }

    [Fact]
    public void Listing_ShowsWhatTheIndexSays_AndClearingKeepsTheList()
    {
        var publisher = NewPublisher();
        Assert.True(publisher.TrySign(PublicationCandidates.Simple(), fixture.Persona.Slot, fixture.Persona.PublicKey));

        var fresh = NewPublisher();
        Assert.True(fresh.TryList(fixture.Persona.Slot, fixture.Persona.PublicKey));
        Assert.Equal(OutboxState.Waiting, Assert.Single(fresh.View.Publications!.Entries).Outbox);
        Assert.Null(fresh.View.LastOutcome);

        publisher.ClearOutcome();
        Assert.Null(publisher.View.LastOutcome);
        Assert.Single(publisher.View.Publications!.Entries);
    }

    [Fact]
    public void WorkThatThrows_LeavesThePublisherIdle()
    {
        var publisher = new SharePublisher(
            (_, work) =>
            {
                try
                {
                    work(fixture.Personas);
                }
                catch (InvalidOperationException)
                {
                    // The session logs the kind; the publisher's view must not stay busy.
                }

                return true;
            },
            new PublicationFiles(fixture.Root),
            () => throw new InvalidOperationException("clock"),
            log.Add);

        Assert.True(publisher.TrySign(PublicationCandidates.Simple(), fixture.Persona.Slot, fixture.Persona.PublicKey));
        Assert.False(publisher.View.Busy);
    }

    private SharePublisher NewPublisher() => new(RunNow(fixture.Personas), fixture.Files, () => fixture.Now, log.Add);

    /// <summary>A stand-in for the persona session's TryRun: the work runs at once, unless the session is busy.</summary>
    private Func<string, Action<PersonaManager>, bool> RunNow(PersonaManager personas) => (name, work) =>
    {
        if (sessionBusy)
        {
            return false;
        }

        ran.Add(name);
        work(personas);
        return true;
    };
}
