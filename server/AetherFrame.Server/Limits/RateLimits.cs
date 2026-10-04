using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace AetherFrame.Server.Limits;

/// <summary>One limit: at most <paramref name="Count"/> events in any <paramref name="Window"/>.</summary>
internal sealed record Limit(string Name, int Count, TimeSpan Window);

/// <summary>
/// The limits of decision C6, and the ones batch C leaves to N2-7 (recorded in ServerApi-v1.md). An
/// address limit applies to each of the address's groups (<see cref="AddressGroups"/>): the /32 of an
/// IPv4 address, and the /64, /56 and /48 of an IPv6 one, at 1, 4 and 16 times the limit.
/// </summary>
internal static class ServerLimits
{
    public static readonly Limit ChallengesPerAddress = new("challenge/address", 600, TimeSpan.FromHours(1));

    public static readonly Limit LodestonePerKey = new("lodestone/key", 10, TimeSpan.FromHours(1));

    public static readonly Limit LodestonePerAddress = new("lodestone/address", 10, TimeSpan.FromHours(1));

    public static readonly Limit ChecksPerLodestoneId = new("check/lodestone-id", 10, TimeSpan.FromDays(1));

    public static readonly Limit OptOutsPerKey = new("opt-out/key", 10, TimeSpan.FromHours(1));

    public static readonly Limit OptOutsPerAddress = new("opt-out/address", 30, TimeSpan.FromHours(1));

    public static readonly Limit LookupsPerKeyHour = new("lookup/key/hour", 120, TimeSpan.FromHours(1));

    public static readonly Limit LookupsPerKeyDay = new("lookup/key/day", 600, TimeSpan.FromDays(1));

    public static readonly Limit LookupsPerAddress = new("lookup/address", 300, TimeSpan.FromHours(1));

    public static readonly Limit ImagesPerKey = new("image/key", 8 * 120, TimeSpan.FromHours(1));

    public static readonly Limit ImagesPerAddress = new("image/address", 8 * 300, TimeSpan.FromHours(1));

    public static readonly Limit PublishesPerCharacter = new("publish/character", 60, TimeSpan.FromHours(1));

    public static readonly Limit PublishesPerAddress = new("publish/address", 120, TimeSpan.FromHours(1));

    /// <summary>
    /// Images a character may send through the worker in an hour (the open alpha, October 1, 2026):
    /// four full publishes of eight, so one character can't keep the one worker to itself.
    /// </summary>
    public static readonly Limit ImageJobsPerCharacter = new("image-job/character", 32, TimeSpan.FromHours(1));

    /// <summary>
    /// Images of a character the worker failed on (refused, crashed or stalled) in an hour: each may
    /// hold the worker's one pipeline for a whole run, so after three the character's publishes with
    /// images wait out the hour.
    /// </summary>
    public static readonly Limit WorkerFailuresPerCharacter = new("worker-failure/character", 3, TimeSpan.FromHours(1));

    public static readonly Limit ReportsPerKey = new("report/key", 20, TimeSpan.FromDays(1));

    public static readonly Limit ReportsPerAddress = new("report/address", 60, TimeSpan.FromDays(1));

    /// <summary>
    /// Signed starts of a presence session for one key ("The online count"): one at login and one
    /// an hour after (the session's lifetime) is the plugin's normal pace, so 12 leaves room for
    /// restarts of the server or the game without letting a key churn sessions.
    /// </summary>
    public static readonly Limit PresenceStartsPerKey = new("presence-start/key", 12, TimeSpan.FromHours(1));

    /// <summary>
    /// Signed starts of a presence session from one address group: a tenth of
    /// <see cref="ChallengesPerAddress"/>, so presence never takes more than that share of the
    /// challenges that publishing, looking up and checking from the same network also need.
    /// </summary>
    public static readonly Limit PresenceStartsPerAddress = new("presence-start/address", 60, TimeSpan.FromHours(1));

    /// <summary>
    /// Heartbeats and leaves from one address group, counted in a minute so the counter holds few
    /// events: some 100 players behind one IPv4 address at the plugin's pace. Its own counter, so a
    /// busy network's heartbeats never take from any other limit.
    /// </summary>
    public static readonly Limit PresenceBeatsPerAddress = new("presence/address", 120, TimeSpan.FromMinutes(1));
}

/// <summary>
/// Groups an address the way the limits count it (decision R4): an IPv4-mapped IPv6 address as its
/// IPv4 address, an IPv4 address as its /32, and an IPv6 address as its /64, /56 and /48, since one
/// site can hold many /64s (RFC 6177). Each group is paired with its multiple of the limit.
/// </summary>
internal static class AddressGroups
{
    public static IEnumerable<(string Group, int Multiple)> Of(IPAddress? address)
    {
        if (address is null)
        {
            yield return ("none", 1);
            yield break;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            yield return ("4/" + address, 1);
            yield break;
        }

        var bytes = address.GetAddressBytes();
        yield return ("6/64/" + Convert.ToHexString(bytes, 0, 8), 1);
        yield return ("6/56/" + Convert.ToHexString(bytes, 0, 7), 4);
        yield return ("6/48/" + Convert.ToHexString(bytes, 0, 6), 16);
    }
}

/// <summary>
/// Sliding-window counters, in memory only, for as long as their window (decisions S5 and C7): no
/// address, key or id they count is written anywhere or logged. Counting is per (limit, subject);
/// a subject is a key's identity, a Lodestone id or an address group. A counter is dropped once all
/// its events have left its own limit's window.
/// </summary>
internal sealed class RateLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<(string Limit, string Subject), Counter> counters = new();
    private DateTimeOffset lastSweep;

    /// <summary>Whether <paramref name="limit"/> has room for one more event for <paramref name="subject"/>, without counting one.</summary>
    public bool HasRoom(Limit limit, string subject)
    {
        if (!counters.TryGetValue((limit.Name, subject), out var counter))
        {
            return true;
        }

        lock (counter)
        {
            counter.Trim(time.GetUtcNow());
            return counter.Removed || counter.Events.Count < limit.Count;
        }
    }

    /// <summary>Counts one event against <paramref name="limit"/> for <paramref name="subject"/>, unless it is already at the limit.</summary>
    public bool TryTake(Limit limit, string subject, int multiple = 1)
    {
        var now = time.GetUtcNow();
        Sweep(now);
        while (true)
        {
            var counter = counters.GetOrAdd((limit.Name, subject), static (_, window) => new Counter(window), limit.Window);
            lock (counter)
            {
                // A counter the sweep has just dropped is replaced, so no event is counted in a lost one.
                if (counter.Removed)
                {
                    continue;
                }

                counter.Trim(now);
                if (counter.Events.Count >= limit.Count * multiple)
                {
                    return false;
                }

                counter.Events.Enqueue(now);
                return true;
            }
        }
    }

    /// <summary>Counts one event against <paramref name="limit"/> for every group of <paramref name="address"/>; all must have room.</summary>
    public bool TryTakeAddress(Limit limit, IPAddress? address)
    {
        foreach (var (group, multiple) in AddressGroups.Of(address))
        {
            if (!TryTake(limit, group, multiple))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>How many counters are held: for tests of the memory bound.</summary>
    internal int Count => counters.Count;

    /// <summary>Drops every counter whose events have all left its window, at most once a minute.</summary>
    private void Sweep(DateTimeOffset now)
    {
        if (now - lastSweep < TimeSpan.FromMinutes(1))
        {
            return;
        }

        lastSweep = now;
        foreach (var (key, counter) in counters)
        {
            lock (counter)
            {
                counter.Trim(now);
                if (counter.Events.Count == 0)
                {
                    counter.Removed = true;
                    counters.TryRemove(new KeyValuePair<(string, string), Counter>(key, counter));
                }
            }
        }
    }

    private sealed class Counter(TimeSpan window)
    {
        public Queue<DateTimeOffset> Events { get; } = new();

        public bool Removed { get; set; }

        public void Trim(DateTimeOffset now)
        {
            while (Events.Count > 0 && now - Events.Peek() >= window)
            {
                Events.Dequeue();
            }
        }
    }
}
