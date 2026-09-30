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

    public static readonly Limit LookupsPerKeyHour = new("lookup/key/hour", 120, TimeSpan.FromHours(1));

    public static readonly Limit LookupsPerKeyDay = new("lookup/key/day", 600, TimeSpan.FromDays(1));

    public static readonly Limit LookupsPerAddress = new("lookup/address", 300, TimeSpan.FromHours(1));

    public static readonly Limit ImagesPerKey = new("image/key", 8 * 120, TimeSpan.FromHours(1));

    public static readonly Limit ImagesPerAddress = new("image/address", 8 * 300, TimeSpan.FromHours(1));

    public static readonly Limit PublishesPerCharacter = new("publish/character", 60, TimeSpan.FromHours(1));

    public static readonly Limit ReportsPerKey = new("report/key", 20, TimeSpan.FromDays(1));
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
/// a subject is a key's identity, a Lodestone id or an address group.
/// </summary>
internal sealed class RateLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<(string Limit, string Subject), Queue<DateTimeOffset>> events = new();
    private DateTimeOffset lastSweep;

    /// <summary>Counts one event against <paramref name="limit"/> for <paramref name="subject"/>, unless it is already at the limit.</summary>
    public bool TryTake(Limit limit, string subject, int multiple = 1)
    {
        var now = time.GetUtcNow();
        Sweep(now);
        var queue = events.GetOrAdd((limit.Name, subject), static _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= limit.Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= limit.Count * multiple)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
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

    /// <summary>Drops every counter whose events have all left their window, at most once a minute.</summary>
    private void Sweep(DateTimeOffset now)
    {
        if (now - lastSweep < TimeSpan.FromMinutes(1))
        {
            return;
        }

        lastSweep = now;
        foreach (var (key, queue) in events)
        {
            lock (queue)
            {
                if (queue.Count == 0 || now - queue.Peek() >= TimeSpan.FromDays(1))
                {
                    events.TryRemove(key, out _);
                }
            }
        }
    }
}
