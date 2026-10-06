using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Personas;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Requests;
using AetherFrame.Services.Network.Transport;

namespace AetherFrame.Services.Network.Sharing;

/// <summary>What My Plates shows of the online count.</summary>
internal enum OnlineCountState
{
    /// <summary>Nothing: no character is logged in and sharing, so nothing is sent.</summary>
    Off,

    /// <summary>A session is starting and no count has come back yet.</summary>
    Connecting,

    /// <summary>The count the server last answered with, at <see cref="OnlineCountView.At"/>.</summary>
    Online,

    /// <summary>The server didn't answer with a count, or its last count is too old to show.</summary>
    Unavailable,
}

/// <summary>
/// The online count as My Plates draws it: one immutable value, replaced whole. A count is shown
/// only while it is younger than <see cref="OnlineCount.Fresh"/> (<see cref="AsOf"/>); a failed
/// request never becomes a zero.
/// </summary>
internal sealed record OnlineCountView(OnlineCountState State, int Count = 0, DateTimeOffset At = default)
{
    internal static readonly OnlineCountView Off = new(OnlineCountState.Off);

    internal static readonly OnlineCountView Connecting = new(OnlineCountState.Connecting);

    internal static readonly OnlineCountView Unavailable = new(OnlineCountState.Unavailable);

    /// <summary>This view as it should read at <paramref name="now"/>: a count older than <see cref="OnlineCount.Fresh"/> reads as unavailable.</summary>
    internal OnlineCountView AsOf(DateTimeOffset now) =>
        State == OnlineCountState.Online && (now - At >= OnlineCount.Fresh || now < At - OnlineCount.Fresh) ? Unavailable : this;
}

/// <summary>Whose presence is sent: the logged-in character's key, while that character shares.</summary>
internal sealed record PresenceTarget(PersonaSlotId Slot, PersonaId Key);

/// <summary>How often presence requests go, and how they back off: <see cref="Default"/> in the plugin, shorter in tests.</summary>
internal sealed record OnlineCountPace(TimeSpan Beat, TimeSpan Jitter, TimeSpan LongestBackoff, TimeSpan BusyRetry, TimeSpan RestartSpread, TimeSpan LeaveTimeout, TimeSpan StartGrace)
{
    /// <summary>
    /// A heartbeat every 50 to 70 seconds, well inside the server's 180-second expiry; after a
    /// failure, a minute, then twice as long each time, to 15 minutes at most; a key the persona
    /// session is too busy to sign with is asked again after 5 seconds; a session the server no
    /// longer knows is started again within 10 seconds, so a restarted server isn't met by every
    /// plugin at once; a leave gets 3 seconds; and a start already sent gets 1.5 seconds more after
    /// the run stops, so a session the server has made is read and then left (the two together stay
    /// inside the unload's 5 seconds). Each session is renewed 50 to 54 minutes after its start
    /// (<see cref="Renewal"/>).
    /// </summary>
    internal static readonly OnlineCountPace Default = new(
        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1.5));

    /// <summary>
    /// How long after a session's start a new start replaces it, give or take
    /// <see cref="RenewalJitter"/>: inside the hour the server counts a session's heartbeats for,
    /// with room for the heartbeat it waits for and a few tries more, so the server never has to
    /// refuse a heartbeat at the hour, and the character is never missing from the count over the change.
    /// </summary>
    internal TimeSpan Renewal { get; init; } = TimeSpan.FromMinutes(52);

    /// <inheritdoc cref="Renewal"/>
    internal TimeSpan RenewalJitter { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// The online count ("The online count" in docs/networking/DecisionRegister.md, the one exception
/// to R2's "no background traffic"): while a character is logged in and shares, a signed start,
/// then a small heartbeat about once a minute, keep it counted, and each answer carries the count
/// of sharing characters online, which My Plates shows. The server counts a session's heartbeats
/// for an hour, so a new signed start replaces the session before then
/// (<see cref="OnlineCountPace.Renewal"/>). Nothing is sent for a character that doesn't share, or
/// while nobody is logged in.
/// <para>
/// <see cref="Update"/> runs on the framework thread and only compares and hands over: every
/// request runs in a background task, never in a frame. The start's challenge and request are sent
/// outside the persona session, which is held only for the one signature, so presence never holds
/// the session a publish needs, and a busy session only delays presence. The start's challenge is
/// presence's own (<c>/v1/presence/challenge</c>), and heartbeats carry the session's token alone,
/// so presence never takes from the challenges or limits publishing needs; a limit's refusal waits
/// the longest backoff. A takeover's 410 ends the run. A logout, a pause, turning sharing off, a takeover, another character or
/// unloading stops the run, which then ends its session with a leave; a crash leaves it to expire
/// on the server. An answer that comes back after its run stopped changes nothing. The log gets
/// outcome kinds only: never a token, a key or a count. Compiled only in the networking preview
/// flavour.
/// </para>
/// </summary>
internal sealed class OnlineCount
{
    /// <summary>How long a count is shown after the server gave it: the server's expiry of a session.</summary>
    internal static readonly TimeSpan Fresh = TimeSpan.FromSeconds(180);

    /// <summary>
    /// How long a presence challenge is kept for another try at signing: well inside the 300
    /// seconds the server accepts it for, leaving time for the start itself.
    /// </summary>
    internal static readonly TimeSpan ChallengeKept = TimeSpan.FromSeconds(240);

    private readonly SharingClient client;
    private readonly Func<string, Action<PersonaManager>, bool> tryRun;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly CancellationToken stopping;
    private readonly Action<string> log;
    private readonly OnlineCountPace pace;
    private readonly Random random;
    private readonly object gate = new();
    private readonly object randomGate = new();
    private readonly List<Task> running = new();
    private Run? current;
    private bool closed;
    private volatile OnlineCountView view = OnlineCountView.Off;

    internal OnlineCount(
        SharingClient client,
        Func<string, Action<PersonaManager>, bool> tryRun,
        Func<DateTimeOffset> utcNow,
        CancellationToken stopping,
        Action<string> log,
        OnlineCountPace? pace = null,
        Random? random = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.tryRun = tryRun ?? throw new ArgumentNullException(nameof(tryRun));
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        this.stopping = stopping;
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.pace = pace ?? OnlineCountPace.Default;
        this.random = random ?? Random.Shared;
    }

    /// <summary>The count as it stands, for My Plates: read it with <see cref="OnlineCountView.AsOf"/>.</summary>
    internal OnlineCountView View => view;

    /// <summary>
    /// Whose presence the sharing file and the game ask for now: the logged-in character's key,
    /// when that character shares (not paused, turned off, taken over, being checked or moving to
    /// a new key), the sharing file was read, and the player has seen what the online count sends
    /// (<see cref="CharacterSharingView.OnlineNotice"/>). Null otherwise, and then nothing is sent.
    /// </summary>
    internal static PresenceTarget? TargetOf(CharacterSharingView sharing, ulong? loggedIn)
    {
        if (!sharing.Loaded || sharing.Unreadable || sharing.OnlineNotice || loggedIn is not { } contentId || contentId == 0)
        {
            return null;
        }

        return sharing.Find(contentId) is { Stage: SharingStage.Shared, ReplacingKey: false } entry ? new PresenceTarget(entry.Slot, entry.Key) : null;
    }

    /// <summary>
    /// The framework thread, each tick of the game (<see cref="PresenceDriver"/>): when
    /// <paramref name="target"/> differs from the run's, the run stops (and leaves) and, for a
    /// target, a new one starts in the background. No request is made here.
    /// </summary>
    internal void Update(PresenceTarget? target)
    {
        lock (gate)
        {
            if (closed || Equals(current?.Target, target))
            {
                return;
            }

            Stop(current);
            current = null;
            running.RemoveAll(Done);
            if (target is null)
            {
                view = OnlineCountView.Off;
                return;
            }

            var run = new Run(target, CancellationTokenSource.CreateLinkedTokenSource(stopping));
            current = run;
            view = OnlineCountView.Connecting;
            running.Add(Task.Run(() => RunAsync(run)));
        }
    }

    /// <summary>
    /// Unloading: nothing new starts, the run stops and sends its leave, and this waits for every
    /// run to end, within <paramref name="budget"/>. Never throws.
    /// </summary>
    internal async Task StopAsync(TimeSpan budget)
    {
        Task[] pending;
        lock (gate)
        {
            closed = true;
            Stop(current);
            current = null;
            view = OnlineCountView.Off;
            pending = running.ToArray();
            running.Clear();
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(budget).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Unloading goes on whatever a run came to: a run that faulted, or one still going.
            log($"Online count: a run didn't end cleanly within the unload's budget ({exception.GetType().Name}).");
        }
    }

    /// <summary>Whether a run's task, or a cancellation's, is over; a fault is logged by its type, so none goes unobserved.</summary>
    private bool Done(Task task)
    {
        if (!task.IsCompleted)
        {
            return false;
        }

        if (task.Exception is { } fault)
        {
            log($"Online count: a run ended with {fault.GetBaseException().GetType().Name}.");
        }

        return true;
    }

    /// <summary>
    /// Stops a run, doing none of its work here: cancelling it inline would run the run's own
    /// continuation, and so its leave's request, on the thread that called this, which is the game's
    /// own (requirement 8). The cancellation's task is kept, so the unload waits for it and nothing
    /// of it goes unobserved. A run already over is only disposed, which drops its registration on
    /// the plugin's stopping token. Under the lock.
    /// </summary>
    private void Stop(Run? run)
    {
        if (run is null)
        {
            return;
        }

        if (run.Finished)
        {
            run.Stop.Dispose();
            return;
        }

        running.Add(run.Stop.CancelAsync());
    }

    /// <summary>
    /// A run is over: its token source goes, unless it is still the current one and
    /// <see cref="Update"/> may yet cancel it, which then disposes it instead.
    /// </summary>
    private void Finished(Run run)
    {
        lock (gate)
        {
            run.Finished = true;
            if (!ReferenceEquals(current, run))
            {
                run.Stop.Dispose();
            }
        }
    }

    /// <summary>
    /// One run: a start, heartbeats until it stops, then a leave for a session it holds. Each
    /// session is renewed before its hour by a new start in place of a heartbeat.
    /// </summary>
    private async Task RunAsync(Run run)
    {
        byte[]? token = null;
        var failures = 0;
        var restarted = false;
        try
        {
            while (true)
            {
                run.Stop.Token.ThrowIfCancellationRequested();
                Outcome outcome;
                if (token is null)
                {
                    (outcome, token) = await StartAsync(run).ConfigureAwait(false);
                    run.Renewal = token is null ? null : NextRenewal();
                }
                else if (await RenewAsync(run).ConfigureAwait(false) is { } renewed)
                {
                    // A new session in place of the one held, or none after a takeover: no
                    // heartbeat this time.
                    (outcome, token) = renewed;
                    run.Renewal = token is null ? null : NextRenewal();
                }
                else
                {
                    outcome = await BeatAsync(run, token).ConfigureAwait(false);
                    if (outcome == Outcome.Gone)
                    {
                        // The server no longer counts the session's heartbeats (it restarted, the
                        // session's hour ended unrenewed, it was replaced, or it expired): one new
                        // start, spread out, before it counts as a failure.
                        token = null;
                        if (!restarted)
                        {
                            restarted = true;
                            await Task.Delay(Between(TimeSpan.Zero, pace.RestartSpread), run.Stop.Token).ConfigureAwait(false);
                            continue;
                        }

                        outcome = Outcome.Failed;
                    }
                    else if (outcome == Outcome.Counted)
                    {
                        restarted = false;
                    }
                }

                if (outcome == Outcome.Ended)
                {
                    // Another key took the character over: nothing more is sent for this target.
                    Failed(run);
                    return;
                }

                TimeSpan wait;
                if (outcome == Outcome.Busy)
                {
                    wait = pace.BusyRetry;
                }
                else if (outcome == Outcome.Limited)
                {
                    // A limit refused it: the longest wait, not the doubling from a minute, so a
                    // crowded network's plugins don't keep asking.
                    failures++;
                    Failed(run);
                    wait = Between(pace.LongestBackoff * 0.8, pace.LongestBackoff * 1.2);
                }
                else if (outcome == Outcome.Counted)
                {
                    failures = 0;
                    wait = Between(pace.Beat - pace.Jitter, pace.Beat + pace.Jitter);
                }
                else
                {
                    failures++;
                    Failed(run);
                    wait = Backoff(failures);
                }

                await Task.Delay(wait, run.Stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (run.Stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            log($"Online count: the run stopped ({exception.GetType().Name}).");
            Failed(run);
        }
        finally
        {
            if (token is { } held)
            {
                // Never on the thread that stopped the run: whatever cancelled it, the game's own
                // thread makes no request (requirement 8).
                await Task.Run(() => LeaveAsync(held)).ConfigureAwait(false);
            }

            Finished(run);
        }
    }

    /// <summary>
    /// The session's renewal, once it is due (<see cref="OnlineCountPace.Renewal"/>) and while the
    /// session is still live: a new start, which replaces the session on the server, so no heartbeat
    /// of it is refused at its hour. The new session's token with its outcome, or no token for the
    /// takeover that refused it, which ended the session too, so there is nothing to leave. Nothing
    /// when it isn't due or wasn't made, and then the heartbeat goes as usual: a renewal the persona
    /// session was too busy for, or whose challenge was refused, is tried again at the next
    /// heartbeat, and one that failed otherwise isn't, so the hour's refusal starts the session
    /// again, as after a restart of the server.
    /// </summary>
    private async Task<(Outcome Outcome, byte[]? Token)?> RenewAsync(Run run)
    {
        if (run.Renewal is not { } due || !due.DueAt(utcNow()))
        {
            return null;
        }

        var (renewed, token) = await StartAsync(run).ConfigureAwait(false);
        if (token is not null || renewed == Outcome.Ended)
        {
            return (renewed, token);
        }

        if (renewed != Outcome.Busy)
        {
            run.Renewal = null;
        }

        return null;
    }

    /// <summary>
    /// A signed start: a presence challenge, the signature under the persona session, then the
    /// request, both requests outside it. A challenge the session was too busy to sign under is
    /// kept for the next try while it is younger than <see cref="ChallengeKept"/>, so a long
    /// publish doesn't spend one every few seconds.
    /// </summary>
    private async Task<(Outcome Outcome, byte[]? Token)> StartAsync(Run run)
    {
        try
        {
            if (run.Challenge is null || utcNow() - run.ChallengeAt >= ChallengeKept || utcNow() < run.ChallengeAt)
            {
                run.Challenge = await client.PresenceChallengeAsync(run.Stop.Token).ConfigureAwait(false);
                run.ChallengeAt = utcNow();
            }

            var (signed, envelope) = await SignAsync(run, run.Challenge).ConfigureAwait(false);
            if (signed == Outcome.Busy)
            {
                return (signed, null);
            }

            run.Challenge = null;
            if (signed != Outcome.Counted)
            {
                return (signed, null);
            }

            // The start alone is sent with a short grace after the run stops: a session the server
            // has already made is read, so the leave below it can end it, instead of being thrown
            // away with the answer and left counted until the server's expiry.
            using var grace = new CancellationTokenSource();
            using var armed = run.Stop.Token.UnsafeRegister(_ => grace.CancelAfter(pace.StartGrace), null);
            var response = await client.SignedActionAsync(RequestProofKind.Presence, envelope!, grace.Token).ConfigureAwait(false);
            if (response.Status != HttpStatusCode.OK)
            {
                log($"Online count: the start answered {(int)response.Status}.");
                return (response.Status switch
                {
                    HttpStatusCode.Gone => Outcome.Ended,
                    HttpStatusCode.TooManyRequests => Outcome.Limited,

                    // The challenge was refused, or the server saw this start as older than the
                    // character's last change: a fresh challenge and a new signature, shortly.
                    HttpStatusCode.Conflict => Outcome.Busy,
                    _ => Outcome.Failed,
                }, null);
            }

            var (token, online) = SharingWire.ReadPresenceStart(response.Body);

            // A session started for a run that stopped meanwhile is still left, by the run's own leave.
            Counted(run, online);
            return (Outcome.Counted, token);
        }
        catch (SharingException exception)
        {
            log($"Online count: the start got no usable answer ({StatusOf(exception)}).");
            return (exception.Status == HttpStatusCode.TooManyRequests ? Outcome.Limited : Outcome.Failed, null);
        }
        catch (InvalidDataException)
        {
            log("Online count: the start's answer wasn't one.");
            return (Outcome.Failed, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Anything else, a signer's own error say, is one failure, not the end of the run.
            run.Challenge = null;
            log($"Online count: the start failed ({exception.GetType().Name}).");
            return (Outcome.Failed, null);
        }
    }

    /// <summary>
    /// The start's signature, made under the persona session in one short operation: the key is
    /// selected, signs through a lease opened and released at once (L10), and the selection is put
    /// back. Busy when the session runs something else, such as a publish; failed when the key isn't
    /// the registry's or can't sign.
    /// </summary>
    private async Task<(Outcome Outcome, byte[]? Envelope)> SignAsync(Run run, RequestChallenge challenge)
    {
        var signed = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started;
        try
        {
            started = tryRun("presence sign", manager =>
            {
                var selected = manager.Active?.Slot;
                try
                {
                    if (!manager.TryGet(run.Target.Slot, out var persona) || !persona!.PublicKey.Id.Equals(run.Target.Key))
                    {
                        signed.TrySetResult(null);
                        return;
                    }

                    if (manager.Active?.Slot != run.Target.Slot)
                    {
                        manager.Select(run.Target.Slot);
                    }

                    var signer = new LeasedSigner(manager, run.Target.Slot, persona.PublicKey);
                    signed.TrySetResult(client.SignedEnvelope(RequestProofKind.Presence, SharingWire.Empty(), challenge, signer));
                }
                catch (Exception exception) when (exception is LeasedSignerException or PersonaException)
                {
                    signed.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    signed.TrySetException(exception);
                }
                finally
                {
                    CharacterSharing.Reselect(manager, selected, log);
                }
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            started = false;
        }

        if (!started)
        {
            return (Outcome.Busy, null);
        }

        var envelope = await signed.Task.WaitAsync(run.Stop.Token).ConfigureAwait(false);
        if (envelope is null)
        {
            log("Online count: the character's key couldn't sign.");
            return (Outcome.Failed, null);
        }

        return (Outcome.Counted, envelope);
    }

    private async Task<Outcome> BeatAsync(Run run, byte[] token)
    {
        try
        {
            var response = await client.PresenceBeatAsync(token, run.Stop.Token).ConfigureAwait(false);
            if (response.Status == HttpStatusCode.NotFound)
            {
                return Outcome.Gone;
            }

            if (response.Status != HttpStatusCode.OK)
            {
                log($"Online count: a heartbeat answered {(int)response.Status}.");
                return response.Status == HttpStatusCode.TooManyRequests ? Outcome.Limited : Outcome.Failed;
            }

            Counted(run, SharingWire.ReadPresenceBeat(response.Body));
            return Outcome.Counted;
        }
        catch (SharingException exception)
        {
            log($"Online count: a heartbeat got no usable answer ({StatusOf(exception)}).");
            return Outcome.Failed;
        }
        catch (InvalidDataException)
        {
            log("Online count: a heartbeat's answer wasn't one.");
            return Outcome.Failed;
        }
    }

    /// <summary>Ends the session on the server, within <see cref="OnlineCountPace.LeaveTimeout"/> and whatever stopped the run. Never throws.</summary>
    private async Task LeaveAsync(byte[] token)
    {
        using var deadline = new CancellationTokenSource(pace.LeaveTimeout);
        try
        {
            await client.PresenceLeaveAsync(token, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SharingException or OperationCanceledException or ObjectDisposedException)
        {
            // The session expires on the server instead.
        }
    }

    /// <summary>A count came back: shown, if its run is still the current one.</summary>
    private void Counted(Run run, int online)
    {
        lock (gate)
        {
            if (ReferenceEquals(current, run))
            {
                view = new OnlineCountView(OnlineCountState.Online, online, utcNow());
            }
        }
    }

    /// <summary>A request failed: a count still fresh stays shown, and otherwise the view says unavailable, never zero.</summary>
    private void Failed(Run run)
    {
        lock (gate)
        {
            if (ReferenceEquals(current, run) && view.AsOf(utcNow()).State != OnlineCountState.Online)
            {
                view = OnlineCountView.Unavailable;
            }
        }
    }

    /// <summary>The renewal of a session started now: 50 to 54 minutes on, by default.</summary>
    private Renewal NextRenewal() => new(utcNow(), Between(pace.Renewal - pace.RenewalJitter, pace.Renewal + pace.RenewalJitter));

    private TimeSpan Backoff(int failures)
    {
        var longest = pace.LongestBackoff.Ticks;
        var ticks = pace.Beat.Ticks;
        for (var step = 1; step < failures && ticks < longest; step++)
        {
            ticks *= 2;
        }

        var wait = TimeSpan.FromTicks(Math.Min(ticks, longest));
        return Between(wait * 0.8, wait * 1.2);
    }

    private TimeSpan Between(TimeSpan least, TimeSpan most)
    {
        if (most <= least)
        {
            return least < TimeSpan.Zero ? TimeSpan.Zero : least;
        }

        double fraction;
        lock (randomGate)
        {
            fraction = random.NextDouble();
        }

        var wait = least + ((most - least) * fraction);
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    private static string StatusOf(SharingException exception) =>
        exception.Status is { } status ? ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";

    private enum Outcome
    {
        Counted,
        Busy,
        Gone,
        Failed,

        /// <summary>A rate limit refused it (429): the longest wait follows.</summary>
        Limited,

        /// <summary>Another key took the character over (410): the run ends and sends nothing more.</summary>
        Ended,
    }

    /// <summary>
    /// When the session held was started, by the plugin's clock, and how long after that a new start
    /// replaces it: due once that long has passed, or at once if the clock was set back to before
    /// the start, so a session is renewed early rather than after its hour.
    /// </summary>
    private readonly record struct Renewal(DateTimeOffset Since, TimeSpan After)
    {
        internal bool DueAt(DateTimeOffset now) => now - Since >= After || now < Since;
    }

    private sealed class Run(PresenceTarget target, CancellationTokenSource stop)
    {
        internal PresenceTarget Target { get; } = target;

        internal CancellationTokenSource Stop { get; } = stop;

        /// <summary>Whether the run's task is past its leave: only inside the lock.</summary>
        internal bool Finished { get; set; }

        /// <summary>A presence challenge not yet signed under, kept across a busy session; only the run's own task touches it.</summary>
        internal RequestChallenge? Challenge { get; set; }

        internal DateTimeOffset ChallengeAt { get; set; }

        /// <summary>When the session held is renewed; none while no session is held or after a renewal failed. Only the run's own task touches it.</summary>
        internal Renewal? Renewal { get; set; }
    }
}
