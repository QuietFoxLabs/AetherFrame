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
internal sealed record OnlineCountPace(TimeSpan Beat, TimeSpan Jitter, TimeSpan LongestBackoff, TimeSpan BusyRetry, TimeSpan RestartSpread, TimeSpan LeaveTimeout)
{
    /// <summary>
    /// A heartbeat every 50 to 70 seconds, well inside the server's 180-second expiry; after a
    /// failure, a minute, then twice as long each time, to 15 minutes at most; a key the persona
    /// session is too busy to sign with is asked again after 5 seconds; a session the server no
    /// longer knows is started again within 10 seconds, so a restarted server isn't met by every
    /// plugin at once; and a leave gets 3 seconds.
    /// </summary>
    internal static readonly OnlineCountPace Default = new(
        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3));
}

/// <summary>
/// The online count ("The online count" in docs/networking/DecisionRegister.md, the one exception
/// to R2's "no background traffic"): while a character is logged in and shares, a signed start,
/// then a small heartbeat about once a minute, keep it counted, and each answer carries the count
/// of sharing characters online, which My Plates shows. Nothing is sent for a character that
/// doesn't share, or while nobody is logged in.
/// <para>
/// <see cref="Update"/> runs on the framework thread and only compares and hands over: every
/// request runs in a background task, never in a frame. The start's challenge and request are sent
/// outside the persona session, which is held only for the one signature, so presence never holds
/// the session a publish needs, and a busy session only delays presence. Heartbeats carry the
/// session's token alone, so they never take a challenge, and the server counts them against a
/// limit of their own. A logout, a pause, turning sharing off, a takeover, another character or
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
    /// The framework thread, each frame: when <paramref name="target"/> differs from the run's, the
    /// run stops (and leaves) and, for a target, a new one starts in the background. No request is
    /// made here.
    /// </summary>
    internal void Update(PresenceTarget? target)
    {
        lock (gate)
        {
            if (closed || Equals(current?.Target, target))
            {
                return;
            }

            current?.Stop.Cancel();
            current = null;
            running.RemoveAll(task => task.IsCompleted);
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
            current?.Stop.Cancel();
            current = null;
            view = OnlineCountView.Off;
            pending = running.ToArray();
            running.Clear();
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(budget).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            log($"Online count: a run didn't end within the unload's budget ({exception.GetType().Name}).");
        }
    }

    /// <summary>One run: a start, heartbeats until it stops, then a leave for a session it holds.</summary>
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
                }
                else
                {
                    outcome = await BeatAsync(run, token).ConfigureAwait(false);
                    if (outcome == Outcome.Gone)
                    {
                        // The server no longer knows the session (it restarted, the session's hour
                        // ended, or it expired): one new start, spread out, before it counts as a failure.
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

                TimeSpan wait;
                if (outcome == Outcome.Busy)
                {
                    wait = pace.BusyRetry;
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
            // The run's token source is left to the collector: Update may still cancel it.
            if (token is { } held)
            {
                await LeaveAsync(held).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A signed start: a challenge, the signature under the persona session, then the request, both requests outside it.</summary>
    private async Task<(Outcome Outcome, byte[]? Token)> StartAsync(Run run)
    {
        try
        {
            var challenge = await client.ChallengeAsync(run.Stop.Token).ConfigureAwait(false);
            var (signed, envelope) = await SignAsync(run, challenge).ConfigureAwait(false);
            if (signed != Outcome.Counted)
            {
                return (signed, null);
            }

            var response = await client.SignedActionAsync(RequestProofKind.Presence, envelope!, run.Stop.Token).ConfigureAwait(false);
            if (response.Status != HttpStatusCode.OK)
            {
                log($"Online count: the start answered {(int)response.Status}.");
                return (Outcome.Failed, null);
            }

            var (token, online) = SharingWire.ReadPresenceStart(response.Body);

            // A session started for a run that stopped meanwhile is still left, by the run's own leave.
            Counted(run, online);
            return (Outcome.Counted, token);
        }
        catch (SharingException exception)
        {
            log($"Online count: the start got no usable answer ({StatusOf(exception)}).");
            return (Outcome.Failed, null);
        }
        catch (InvalidDataException)
        {
            log("Online count: the start's answer wasn't one.");
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
                return Outcome.Failed;
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
    }

    private sealed class Run(PresenceTarget target, CancellationTokenSource stop)
    {
        internal PresenceTarget Target { get; } = target;

        internal CancellationTokenSource Stop { get; } = stop;
    }
}
