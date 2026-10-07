using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Requests;

namespace AetherFrame.Server.Presence;

/// <summary>What a heartbeat came to.</summary>
internal enum BeatResult
{
    /// <summary>The session is live again for <see cref="PresenceStore.Expiry"/>.</summary>
    Counted,

    /// <summary>
    /// No such session: it never existed, expired, outlived <see cref="PresenceStore.SessionLifetime"/>,
    /// was replaced by its key's newer session, or the server restarted. The plugin starts a new one.
    /// </summary>
    Unknown,

    /// <summary>The session's last heartbeat was under <see cref="PresenceStore.MinimumBeatSpacing"/> ago: it is left as it was.</summary>
    TooSoon,
}

/// <summary>What a start came to.</summary>
internal enum StartResult
{
    /// <summary>A session was started, and its token answered.</summary>
    Started,

    /// <summary>The store is full even after a sweep: nothing was started, and the plugin tries again later.</summary>
    Full,

    /// <summary>
    /// The key's or the character's sharing was revoked after the start's challenge was issued (a
    /// pause, an opt-out, a takeover, a binding removed), so the start may have been signed and sent
    /// on a binding that is already gone. Nothing was started; the plugin signs a new start under a
    /// fresh challenge, which passes only while the binding still stands.
    /// </summary>
    Stale,

    /// <summary>
    /// The start's challenge was issued <see cref="PresenceStore.RevocationMemory"/> or more ago,
    /// however recently it was consumed: past that, a revocation that came after it may already be
    /// forgotten, so nothing is started, and the plugin signs a new start under a fresh challenge.
    /// </summary>
    Expired,
}

/// <summary>
/// What consuming a presence challenge admits: a start signed under it, until
/// <see cref="PresenceStore.RevocationMemory"/> after the challenge was issued. Its place in the
/// order the challenges were issued in, and when it was issued by the store's own clock; the default
/// admits nothing.
/// </summary>
internal readonly record struct Admission(long Sequence, TimeSpan IssuedAt);

/// <summary>
/// The online count ("The online count" in docs/networking/DecisionRegister.md): which bound
/// characters have a live presence session, in memory only. A session is started by a signed
/// request (<see cref="Start"/>) and kept by heartbeats that carry only its token
/// (<see cref="Beat"/>), so a heartbeat needs no challenge and costs a dictionary lookup. Nothing
/// here is ever written to disk or logged, and a session's times go with it: a restart forgets
/// every session, and the plugins start new ones at their next heartbeat. Outside the store, a
/// presence request that succeeds leaves no line in the request log, and one that fails leaves
/// its one line (decision S5: its route, status, duration, failure kind and time, no identifier);
/// the rate limiter keeps the time of each start, a renewal included, under the key's identity and
/// its address group, and of each presence challenge under its address group, for an hour.
/// <para>
/// The count is the number of distinct Lodestone ids with a live session, so a character is
/// counted once however many sessions name it (a takeover's two PCs, say). What an answer gives is
/// not the count at that moment but one snapshot of it for each <see cref="SnapshotWindow"/>
/// (<see cref="Snapshot"/>): the count as it stood when the window began, the same for every start
/// and heartbeat in it, whichever request comes first and whenever. The sessions themselves still
/// end at once on a leave or a revocation, and at their expiry. A key holds one session at a time:
/// starting another replaces it. The token itself is never kept, only its SHA-256, so the server's
/// memory holds nothing a heartbeat could be forged from. The store holds at most
/// <see cref="MaxSessions"/> sessions; past that, a start is refused until expired ones are swept.
/// </para>
/// <para>
/// Every time here is read from the store's own clock (<see cref="Now"/>): the monotonic
/// timestamp, counted from when the store was made, read under the lock. Setting the wall clock,
/// either way, moves no expiry, no admission, no revocation and no window.
/// </para>
/// <para>
/// Expired sessions and unused challenges go whether or not anything asks for the count:
/// <see cref="PresenceSweep"/> calls <see cref="SweepExpired"/> every
/// <see cref="PresenceSweep.Interval"/>, so a plugin that crashed is forgotten within
/// <see cref="Expiry"/> plus that interval even on an idle server. A revocation (a pause, an
/// opt-out, a takeover, a binding removed) is remembered for <see cref="RevocationMemory"/>, long
/// enough to refuse a start that was signed before it and arrived after it, and a start is
/// admitted only for that long after its challenge was issued, so no start can outlast the memory
/// that would refuse it (<see cref="Admission"/>).
/// </para>
/// </summary>
internal sealed class PresenceStore(TimeProvider time)
{
    /// <summary>How long a session stays counted after its start or its last counted heartbeat.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(180);

    /// <summary>
    /// How long a session's heartbeats are counted from its start: after that a heartbeat is
    /// <see cref="BeatResult.Unknown"/>, so the binding and the allowlist are checked again by a
    /// signed start at least this often. The plugin signs that start before the hour ends, and the
    /// start replaces the session. A session whose hour ended first is still counted until its
    /// expiry, unless a new start replaces it sooner, so a character is never missing from a window
    /// over the change.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(1);

    /// <summary>The least time between two heartbeats of one session that are both counted.</summary>
    public static readonly TimeSpan MinimumBeatSpacing = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The windows the count is answered in, fixed by the store's clock (every 5 minutes from when
    /// the server started): one snapshot of the count as each begins, given to every start and
    /// heartbeat in it. It holds the characters whose sessions were live at the window's start,
    /// worked out exactly whichever request takes it and whenever, so asking more often, or at a
    /// chosen moment, tells nothing more, and a login or logout can be timed only to the window it
    /// fell in.
    /// </summary>
    public static readonly TimeSpan SnapshotWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a revoked key or character is remembered, so a start signed under a challenge
    /// issued before the revocation is refused: as long as a challenge can be accepted for, and as
    /// long as a start is admitted after its challenge was issued, consumed or not, so no start
    /// signed before a revocation can arrive after the revocation is forgotten.
    /// </summary>
    public static readonly TimeSpan RevocationMemory = AetherFrame.Server.Storage.ChallengeStore.Lifetime;

    /// <summary>The most sessions held at once.</summary>
    public const int MaxSessions = 100_000;

    /// <summary>A session's token: 32 random bytes.</summary>
    public const int TokenLength = 32;

    /// <summary>
    /// The smallest count answered as itself: below it, the answer is 0, which means "fewer than
    /// this" (<see cref="Reported"/>). It hides who comes and goes only while fewer than this many
    /// characters are online in all, and only from a player who adds no characters of their own: a
    /// crossing of the floor, and every change of one from it up, show exactly ("What the floor
    /// doesn't hide" in docs/networking/DecisionRegister.md). The plugin names this number in its
    /// own words, so it changes only with them.
    /// </summary>
    public const int Floor = 5;

    /// <summary>The most presence challenges held at once.</summary>
    public const int MaxChallenges = 10_000;

    /// <summary>
    /// The most revocations remembered one by one; past that one time stands for them all, and every
    /// start whose challenge is older than it is refused until <see cref="RevocationMemory"/> passes.
    /// </summary>
    public const int MaxRevocations = 100_000;

    private readonly object gate = new();
    private readonly Dictionary<string, Session> byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<PersonaId, string> byKey = new();

    // Where the store's clock starts: the monotonic timestamp when it was made.
    private readonly long origin = time.GetTimestamp();

    // Each character with a session held, and how many: its count of entries is the count now.
    private readonly Dictionary<long, int> characters = new();

    // The window the snapshot was taken for, and the count it took: what every answer in that
    // window gives. No window has one until the first is taken.
    private long snapshotWindow = -1;
    private int snapshot;

    // The presence challenges issued and not yet used, by their hex.
    private readonly Dictionary<string, Challenge> challenges = new(StringComparer.Ordinal);

    // Counts the challenges issued, so which of two came first is exact whatever the clock's
    // resolution is. It leaves the server in nothing: no answer, no log, no challenge carries it.
    private long issued;

    // Which key, and which character, stopped sharing, and the last challenge issued when it did:
    // a start under a challenge from before that is refused (Stale). Each is kept for
    // RevocationMemory and nothing longer; past MaxRevocations one revocation stands for them all.
    private readonly Dictionary<PersonaId, Revocation> revokedKeys = new();
    private readonly Dictionary<long, Revocation> revokedCharacters = new();
    private Revocation? revokedAll;

    /// <summary>How many sessions are held, expired ones not yet swept included: for tests of the bound.</summary>
    internal int Sessions
    {
        get
        {
            lock (gate)
            {
                return byToken.Count;
            }
        }
    }

    /// <summary>How many revocations are remembered, the one that stands for all included: for tests of the bound.</summary>
    internal int Revocations
    {
        get
        {
            lock (gate)
            {
                return revokedKeys.Count + revokedCharacters.Count + (revokedAll is null ? 0 : 1);
            }
        }
    }

    /// <summary>How many presence challenges are held, unused and expired alike: for tests of the bound.</summary>
    internal int Challenges
    {
        get
        {
            lock (gate)
            {
                return challenges.Count;
            }
        }
    }

    /// <summary>
    /// Starts a session for <paramref name="key"/>'s character, <paramref name="lodestoneId"/>,
    /// replacing the key's earlier one, under what <see cref="TryConsumeChallenge"/> admitted. Its
    /// token and the window's snapshot of the count, or why it was refused. This is the start's last
    /// step, after its signature and binding were checked, so however long those took, the admission
    /// is weighed here, at the moment the session would be made.
    /// </summary>
    public (StartResult Result, byte[]? Token, int Online) Start(PersonaId key, long lodestoneId, Admission admission)
    {
        lock (gate)
        {
            var now = Enter();

            // Before anything is touched. A start with no challenge of its own admits nothing.
            if (admission.Sequence == 0)
            {
                return (StartResult.Stale, null, 0);
            }

            // A challenge issued RevocationMemory ago or more: a revocation after it may be
            // forgotten by now, so its start is refused whatever happened since, a suspended request
            // included.
            if (Over(admission.IssuedAt, now))
            {
                return (StartResult.Expired, null, 0);
            }

            // A start whose challenge was issued before this key's or this character's last
            // revocation was signed on a binding that may be gone, and the session it would start
            // could outlive the sharing it was made for by the whole expiry.
            if (Stale(key, lodestoneId, admission.Sequence, now))
            {
                return (StartResult.Stale, null, 0);
            }

            RemoveKey(key);
            if (byToken.Count >= MaxSessions)
            {
                Sweep(now);
                if (byToken.Count >= MaxSessions)
                {
                    return (StartResult.Full, null, 0);
                }
            }

            var token = RandomNumberGenerator.GetBytes(TokenLength);
            var id = IdOf(token);
            byToken[id] = new Session(key, lodestoneId, now, now);
            byKey[key] = id;
            characters[lodestoneId] = characters.GetValueOrDefault(lodestoneId) + 1;
            return (StartResult.Started, token, snapshot);
        }
    }

    /// <summary>A heartbeat with <paramref name="token"/>: what it came to, and the window's snapshot of the count when it was counted.</summary>
    public (BeatResult Result, int Online) Beat(ReadOnlySpan<byte> token)
    {
        if (token.Length != TokenLength)
        {
            return (BeatResult.Unknown, 0);
        }

        var id = IdOf(token);
        lock (gate)
        {
            var now = Enter();
            if (!byToken.TryGetValue(id, out var session))
            {
                return (BeatResult.Unknown, 0);
            }

            if (!Live(session, now))
            {
                Remove(id, session);
                return (BeatResult.Unknown, 0);
            }

            // Past its hour the session's heartbeats aren't counted, so the plugin signs a new start;
            // the session itself is kept, and counted until its expiry or until that start replaces
            // it, so the character isn't missing from a window over the change.
            if (now - session.Started >= SessionLifetime)
            {
                return (BeatResult.Unknown, 0);
            }

            if (now - session.LastBeat < MinimumBeatSpacing)
            {
                return (BeatResult.TooSoon, 0);
            }

            byToken[id] = session with { LastBeat = now };
            return (BeatResult.Counted, snapshot);
        }
    }

    /// <summary>
    /// Ends the session with <paramref name="token"/> at once, if there is one: whether it was still
    /// counted. One that expired and wasn't swept yet goes too, but counted for nothing, so its leave
    /// takes from the limit as an unknown token's does. The leave is answered the same either way;
    /// this tells the endpoint only whether the request takes from the address group's limit.
    /// </summary>
    public bool Leave(ReadOnlySpan<byte> token)
    {
        if (token.Length != TokenLength)
        {
            return false;
        }

        var id = IdOf(token);
        lock (gate)
        {
            var now = Enter();
            if (!byToken.TryGetValue(id, out var session))
            {
                return false;
            }

            Remove(id, session);
            return Live(session, now);
        }
    }

    /// <summary>
    /// Ends <paramref name="key"/>'s session, if it has one, and refuses a start of its that was
    /// signed before now: its character's sharing paused, turned off, or its binding removed.
    /// </summary>
    public void ForgetKey(PersonaId key)
    {
        lock (gate)
        {
            var now = Enter();
            RemoveKey(key);
            Revoke(now, key, null);
        }
    }

    /// <summary>
    /// Ends every session for <paramref name="lodestoneId"/> but <paramref name="keep"/>'s, and
    /// refuses a start for that character signed before now: another key's check took the character
    /// over (C1), so the old key's sessions stop counting it, and one already in flight starts
    /// nothing.
    /// </summary>
    public void ForgetOtherKeys(long lodestoneId, PersonaId keep)
    {
        lock (gate)
        {
            var now = Enter();
            List<(string, Session)>? stale = null;
            foreach (var (id, session) in byToken)
            {
                if (session.LodestoneId == lodestoneId && !session.Key.Equals(keep))
                {
                    (stale ??= []).Add((id, session));
                }
            }

            foreach (var (id, session) in stale ?? [])
            {
                Remove(id, session);
            }

            Revoke(now, null, lodestoneId);
        }
    }

    /// <summary>
    /// A challenge for a presence start alone, from <c>/v1/presence/challenge</c>: held in memory
    /// for <see cref="AetherFrame.Server.Storage.ChallengeStore.Lifetime"/>, accepted once and only
    /// by <see cref="TryConsumeChallenge"/>, so presence never takes from the challenges that
    /// publishing, looking up and checking need, and no other action accepts one. Null when
    /// <see cref="MaxChallenges"/> are held even after expired ones are cleared.
    /// </summary>
    public RequestChallenge? IssueChallenge()
    {
        lock (gate)
        {
            var now = Now();
            if (challenges.Count >= MaxChallenges)
            {
                SweepChallenges(now);
                if (challenges.Count >= MaxChallenges)
                {
                    return null;
                }
            }

            var challenge = RequestChallenge.NewRandom();
            challenges[Convert.ToHexString(challenge.ToArray())] = new Challenge(++issued, now);
            return challenge;
        }
    }

    /// <summary>
    /// Consumes a presence challenge: issued here, unexpired and unused, then removed, with
    /// <paramref name="admission"/> what it admits: its place in the order they were issued in,
    /// which <see cref="Start"/> weighs against the revocations it remembers, and when it was
    /// issued, which bounds how late that start may come. False otherwise, and then nothing was
    /// consumed and <paramref name="admission"/> admits nothing.
    /// </summary>
    public bool TryConsumeChallenge(RequestChallenge challenge, out Admission admission)
    {
        admission = default;
        var id = Convert.ToHexString(challenge.ToArray());
        lock (gate)
        {
            var now = Now();
            if (!challenges.TryGetValue(id, out var held) || Over(held.IssuedAt, now))
            {
                return false;
            }

            challenges.Remove(id);
            admission = new Admission(held.Sequence, held.IssuedAt);
            return true;
        }
    }

    /// <summary>
    /// A count as an answer gives it: itself from <see cref="Floor"/> up, and 0, meaning "fewer than
    /// <see cref="Floor"/>", below it.
    /// </summary>
    public static int Reported(int online) => online < Floor ? 0 : online;

    /// <summary>
    /// The count now, which no answer gives (they give the window's <see cref="Snapshot"/>):
    /// distinct characters with a live session. For tests.
    /// </summary>
    internal int Online()
    {
        lock (gate)
        {
            Sweep(Enter());
            return characters.Count;
        }
    }

    /// <summary>The window's snapshot of the count, as every answer in it gives it. For tests.</summary>
    internal int Snapshot()
    {
        lock (gate)
        {
            Enter();
            return snapshot;
        }
    }

    /// <summary>
    /// Drops every expired session, unused challenge and remembered revocation now, whether or not
    /// anything asked for the count: on a server nobody is asking, a plugin that crashed or lost its
    /// network is still forgotten within <see cref="Expiry"/> and a sweep's interval, instead of
    /// sitting in memory until the next request (<see cref="PresenceSweep"/>).
    /// </summary>
    public void SweepExpired()
    {
        lock (gate)
        {
            var now = Enter();
            Sweep(now);
            SweepChallenges(now);
        }
    }

    private static string IdOf(ReadOnlySpan<byte> token) => Convert.ToHexString(SHA256.HashData(token));

    /// <summary>
    /// Whether a session is counted at <paramref name="at"/>: within <see cref="Expiry"/> of its
    /// start or its last counted heartbeat. Heartbeats are counted only within
    /// <see cref="SessionLifetime"/> of the start, so no session is live for longer than that and
    /// <see cref="Expiry"/>.
    /// </summary>
    private static bool Live(Session session, TimeSpan at) => at - session.LastBeat < Expiry;

    /// <summary>
    /// Whether a challenge issued at <paramref name="at"/> is past its lifetime at
    /// <paramref name="now"/>: then it is no longer consumed, and a start it admitted is no longer
    /// started.
    /// </summary>
    private static bool Over(TimeSpan at, TimeSpan now) =>
        now - at >= AetherFrame.Server.Storage.ChallengeStore.Lifetime;

    /// <summary>
    /// The store's own clock: the time since the store was made, by the monotonic timestamp, which
    /// only goes forward and which setting the wall clock doesn't move. Read under the lock, so the
    /// order of the times the store holds is the order it saw things happen in.
    /// </summary>
    private TimeSpan Now() => time.GetElapsedTime(origin);

    /// <summary>
    /// The clock read for one operation on the sessions, under the lock, after the window's snapshot
    /// is taken if it has none yet: so nothing that operation does, or any later one, changes what
    /// the window shows.
    /// </summary>
    private TimeSpan Enter()
    {
        var now = Now();
        var window = now.Ticks / SnapshotWindow.Ticks;
        if (window > snapshotWindow)
        {
            snapshotWindow = window;
            snapshot = CountAt(TimeSpan.FromTicks(window * SnapshotWindow.Ticks));
        }

        return now;
    }

    /// <summary>
    /// The count as it stood at <paramref name="begins"/>, a window's start, for its snapshot: the
    /// characters with a session started before it and live at it. It is exact whenever it is worked
    /// out, since every operation that changes the sessions takes the window's snapshot first
    /// (<see cref="Enter"/>): every session that was live then is still held, with the times it had
    /// then. Under the lock.
    /// </summary>
    private int CountAt(TimeSpan begins)
    {
        var counted = new HashSet<long>();
        foreach (var session in byToken.Values)
        {
            if (session.Started < begins && Live(session, begins))
            {
                counted.Add(session.LodestoneId);
            }
        }

        return counted.Count;
    }

    private void Sweep(TimeSpan now)
    {
        List<(string, Session)>? expired = null;
        foreach (var (id, session) in byToken)
        {
            if (!Live(session, now))
            {
                (expired ??= []).Add((id, session));
            }
        }

        foreach (var (id, session) in expired ?? [])
        {
            Remove(id, session);
        }

        List<PersonaId>? keys = null;
        foreach (var (key, revocation) in revokedKeys)
        {
            if (Forgettable(revocation, now))
            {
                (keys ??= []).Add(key);
            }
        }

        foreach (var key in keys ?? [])
        {
            revokedKeys.Remove(key);
        }

        List<long>? ids = null;
        foreach (var (lodestoneId, revocation) in revokedCharacters)
        {
            if (Forgettable(revocation, now))
            {
                (ids ??= []).Add(lodestoneId);
            }
        }

        foreach (var lodestoneId in ids ?? [])
        {
            revokedCharacters.Remove(lodestoneId);
        }

        if (revokedAll is { } all && Forgettable(all, now))
        {
            revokedAll = null;
        }
    }

    /// <summary>Drops the challenges past their lifetime. Under the lock.</summary>
    private void SweepChallenges(TimeSpan now)
    {
        List<string>? expired = null;
        foreach (var (id, held) in challenges)
        {
            if (Over(held.IssuedAt, now))
            {
                (expired ??= []).Add(id);
            }
        }

        foreach (var id in expired ?? [])
        {
            challenges.Remove(id);
        }
    }

    /// <summary>
    /// Whether a revocation can go: no start under a challenge issued before it is admitted any more
    /// (<see cref="Over"/>, since the store's clock only goes forward, so it is dated no earlier
    /// than those challenges).
    /// </summary>
    private static bool Forgettable(Revocation revocation, TimeSpan now) =>
        now - revocation.At >= RevocationMemory;

    /// <summary>
    /// Remembers that a key, or a character, stopped sharing now, and which challenges were issued
    /// before it did. The store's clock only goes forward, so it is dated no earlier than any of
    /// those challenges and is never forgotten while a start under one of them is still admitted.
    /// Past <see cref="MaxRevocations"/> one revocation stands for them all, so the memory is bounded
    /// and the refusal is still the safe way round. Under the lock.
    /// </summary>
    private void Revoke(TimeSpan now, PersonaId? key, long? lodestoneId)
    {
        var revocation = new Revocation(issued, now);
        if (revokedKeys.Count + revokedCharacters.Count >= MaxRevocations)
        {
            Sweep(now);
            if (revokedKeys.Count + revokedCharacters.Count >= MaxRevocations)
            {
                revokedAll = revocation;
                return;
            }
        }

        if (key is { } revokedKey)
        {
            revokedKeys[revokedKey] = revocation;
        }

        if (lodestoneId is { } revokedCharacter)
        {
            revokedCharacters[revokedCharacter] = revocation;
        }
    }

    /// <summary>
    /// Whether a start for <paramref name="key"/> and <paramref name="lodestoneId"/>, signed under
    /// the challenge numbered <paramref name="challengeIssuedAs"/>, comes from before a revocation
    /// this store remembers. A start with no challenge of its own (0) counts as stale, so the safe
    /// answer is the default. Under the lock.
    /// </summary>
    private bool Stale(PersonaId key, long lodestoneId, long challengeIssuedAs, TimeSpan now) =>
        Before(revokedAll, challengeIssuedAs, now)
        || (revokedKeys.TryGetValue(key, out var forKey) && Before(forKey, challengeIssuedAs, now))
        || (revokedCharacters.TryGetValue(lodestoneId, out var forCharacter) && Before(forCharacter, challengeIssuedAs, now));

    /// <summary>
    /// Whether <paramref name="challengeIssuedAs"/> was issued no later than
    /// <paramref name="revocation"/>, which is still remembered: the challenges are counted, so this
    /// holds however coarse the clock is.
    /// </summary>
    private static bool Before(Revocation? revocation, long challengeIssuedAs, TimeSpan now) =>
        revocation is { } at && challengeIssuedAs <= at.Through && !Forgettable(at, now);

    private void RemoveKey(PersonaId key)
    {
        if (byKey.TryGetValue(key, out var id) && byToken.TryGetValue(id, out var session))
        {
            Remove(id, session);
        }
    }

    private void Remove(string id, Session session)
    {
        byToken.Remove(id);
        if (byKey.TryGetValue(session.Key, out var current) && current == id)
        {
            byKey.Remove(session.Key);
        }

        if (characters.GetValueOrDefault(session.LodestoneId) <= 1)
        {
            characters.Remove(session.LodestoneId);
        }
        else
        {
            characters[session.LodestoneId]--;
        }
    }

    /// <summary>A session: whose, which character, and when it started and last beat, by the store's clock.</summary>
    private sealed record Session(PersonaId Key, long LodestoneId, TimeSpan Started, TimeSpan LastBeat);

    /// <summary>A challenge issued and not yet used: its place in the order, and when it was issued.</summary>
    private sealed record Challenge(long Sequence, TimeSpan IssuedAt);

    /// <summary>A revocation: the last challenge issued when it happened, and when, so it can be forgotten.</summary>
    private sealed record Revocation(long Through, TimeSpan At);
}

/// <summary>
/// Sweeps presence in the background, every <see cref="Interval"/>: expiry is what the player was
/// told ("the server then forgets the character within about 3 minutes"), so it can't wait for the
/// next request to come in. On a server nobody is asking, a session whose plugin crashed is held
/// for at most <see cref="PresenceStore.Expiry"/> plus this interval, and the rate limiter's
/// counters, which hold the times requests were counted at, are dropped as soon as their windows
/// have passed instead of at the next request.
/// </summary>
internal sealed class PresenceSweep(PresenceStore presence, AetherFrame.Server.Limits.RateLimiter limiter, TimeProvider time) : Microsoft.Extensions.Hosting.BackgroundService
{
    /// <summary>How often the sweep runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                presence.SweepExpired();
                limiter.SweepNow();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The next sweep tries again; nothing here can leave the store inconsistent.
            }

            await Task.Delay(Interval, time, stoppingToken);
        }
    }
}
