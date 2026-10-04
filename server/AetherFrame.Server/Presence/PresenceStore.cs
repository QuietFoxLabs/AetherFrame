using System;
using System.Collections.Generic;
using System.Security.Cryptography;
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

/// <summary>
/// The online count ("The online count" in docs/networking/DecisionRegister.md): which bound
/// characters have a live presence session, in memory only. A session is started by a signed
/// request (<see cref="Start"/>) and kept by heartbeats that carry only its token
/// (<see cref="Beat"/>), so a heartbeat needs no challenge and costs a dictionary lookup. Nothing
/// here is ever written to disk or logged, and nothing keeps a time once its session ends: a
/// restart forgets every session, and the plugins start new ones at their next heartbeat.
/// <para>
/// The count is the number of distinct Lodestone ids with a live session, so a character is
/// counted once however many sessions name it (a takeover's two PCs, say). A key holds one session
/// at a time: starting another replaces it. The token itself is never kept, only its SHA-256, so
/// the server's memory holds nothing a heartbeat could be forged from. The store holds at most
/// <see cref="MaxSessions"/> sessions; past that, a start is refused until expired ones are swept.
/// </para>
/// </summary>
internal sealed class PresenceStore(TimeProvider time)
{
    /// <summary>How long a session stays counted after its start or its last heartbeat.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(180);

    /// <summary>
    /// How long a session lasts from its start, whatever its heartbeats: after that, a heartbeat is
    /// <see cref="BeatResult.Unknown"/> and the plugin signs a new start, so the binding and the
    /// allowlist are checked again at least this often.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(1);

    /// <summary>The least time between two heartbeats of one session that are both counted.</summary>
    public static readonly TimeSpan MinimumBeatSpacing = TimeSpan.FromSeconds(20);

    /// <summary>How long after a sweep of expired sessions the count is given without another.</summary>
    public static readonly TimeSpan CountReuse = TimeSpan.FromSeconds(5);

    /// <summary>The most sessions held at once.</summary>
    public const int MaxSessions = 100_000;

    /// <summary>A session's token: 32 random bytes.</summary>
    public const int TokenLength = 32;

    /// <summary>
    /// The smallest count answered as itself: below it, the answer is 0, which means "fewer than
    /// this", so a handful of players who know each other can't watch one another log in and out
    /// (<see cref="Reported"/>).
    /// </summary>
    public const int Floor = 5;

    /// <summary>The most presence challenges held at once.</summary>
    public const int MaxChallenges = 10_000;

    private readonly object gate = new();
    private readonly Dictionary<string, Session> byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<PersonaId, string> byKey = new();

    // Each character with a session held, and how many: its count of entries is the online count.
    private readonly Dictionary<long, int> characters = new();
    private DateTimeOffset sweptAt = DateTimeOffset.MinValue;

    // The presence challenges issued and not yet used, by their hex, with when each stops being accepted.
    private readonly Dictionary<string, DateTimeOffset> challenges = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Starts a session for <paramref name="key"/>'s character, <paramref name="lodestoneId"/>,
    /// replacing the key's earlier one. Its token, and the count with it; null when the store is
    /// full even after a sweep.
    /// </summary>
    public (byte[] Token, int Online)? Start(PersonaId key, long lodestoneId)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            RemoveKey(key);
            if (byToken.Count >= MaxSessions)
            {
                Sweep(now);
                if (byToken.Count >= MaxSessions)
                {
                    return null;
                }
            }

            var token = RandomNumberGenerator.GetBytes(TokenLength);
            var id = IdOf(token);
            byToken[id] = new Session(key, lodestoneId, now, now);
            byKey[key] = id;
            characters[lodestoneId] = characters.GetValueOrDefault(lodestoneId) + 1;
            return (token, CountLocked(now));
        }
    }

    /// <summary>A heartbeat with <paramref name="token"/>: what it came to, and the count when it was counted.</summary>
    public (BeatResult Result, int Online) Beat(ReadOnlySpan<byte> token)
    {
        if (token.Length != TokenLength)
        {
            return (BeatResult.Unknown, 0);
        }

        var now = time.GetUtcNow();
        var id = IdOf(token);
        lock (gate)
        {
            if (!byToken.TryGetValue(id, out var session))
            {
                return (BeatResult.Unknown, 0);
            }

            if (!Live(session, now))
            {
                Remove(id, session);
                return (BeatResult.Unknown, 0);
            }

            if (now - session.LastBeat < MinimumBeatSpacing)
            {
                return (BeatResult.TooSoon, 0);
            }

            byToken[id] = session with { LastBeat = now };
            return (BeatResult.Counted, CountLocked(now));
        }
    }

    /// <summary>Ends the session with <paramref name="token"/> at once, if there is one. Answers nothing either way.</summary>
    public void Leave(ReadOnlySpan<byte> token)
    {
        if (token.Length != TokenLength)
        {
            return;
        }

        var id = IdOf(token);
        lock (gate)
        {
            if (byToken.TryGetValue(id, out var session))
            {
                Remove(id, session);
            }
        }
    }

    /// <summary>Ends <paramref name="key"/>'s session, if it has one: its character's sharing paused or turned off.</summary>
    public void ForgetKey(PersonaId key)
    {
        lock (gate)
        {
            RemoveKey(key);
        }
    }

    /// <summary>
    /// Ends every session for <paramref name="lodestoneId"/> but <paramref name="keep"/>'s: another
    /// key's check took the character over (C1), so the old key's sessions stop counting it.
    /// </summary>
    public void ForgetOtherKeys(long lodestoneId, PersonaId keep)
    {
        lock (gate)
        {
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
        var now = time.GetUtcNow();
        lock (gate)
        {
            if (challenges.Count >= MaxChallenges)
            {
                List<string>? expired = null;
                foreach (var (id, until) in challenges)
                {
                    if (until <= now)
                    {
                        (expired ??= []).Add(id);
                    }
                }

                foreach (var id in expired ?? [])
                {
                    challenges.Remove(id);
                }

                if (challenges.Count >= MaxChallenges)
                {
                    return null;
                }
            }

            var challenge = RequestChallenge.NewRandom();
            challenges[Convert.ToHexString(challenge.ToArray())] = now + AetherFrame.Server.Storage.ChallengeStore.Lifetime;
            return challenge;
        }
    }

    /// <summary>Consumes a presence challenge: issued here, unexpired and unused, then removed. False otherwise, and then nothing was consumed.</summary>
    public bool TryConsumeChallenge(RequestChallenge challenge)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            var id = Convert.ToHexString(challenge.ToArray());
            if (!challenges.TryGetValue(id, out var until) || until <= now)
            {
                return false;
            }

            challenges.Remove(id);
            return true;
        }
    }

    /// <summary>
    /// A count as an answer gives it: itself from <see cref="Floor"/> up, and 0, meaning "fewer than
    /// <see cref="Floor"/>", below it.
    /// </summary>
    public static int Reported(int online) => online < Floor ? 0 : online;

    /// <summary>The count now: distinct characters with a live session.</summary>
    public int Online()
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            return CountLocked(now);
        }
    }

    private static string IdOf(ReadOnlySpan<byte> token) => Convert.ToHexString(SHA256.HashData(token));

    private static bool Live(Session session, DateTimeOffset now) =>
        now - session.LastBeat < Expiry && now - session.Started < SessionLifetime;

    /// <summary>
    /// The count: the characters with a session held, after expired sessions are swept, at most once
    /// every <see cref="CountReuse"/>, so a session can stay counted that long past its expiry. Under
    /// the lock.
    /// </summary>
    private int CountLocked(DateTimeOffset now)
    {
        if (now < sweptAt || now - sweptAt >= CountReuse)
        {
            Sweep(now);
        }

        return characters.Count;
    }

    private void Sweep(DateTimeOffset now)
    {
        sweptAt = now;
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
    }

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

    private sealed record Session(PersonaId Key, long LodestoneId, DateTimeOffset Started, DateTimeOffset LastBeat);
}
