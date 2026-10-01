using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AetherFrame.Protocol.Identity;
using AetherFrame.Server.Endpoints;
using Xunit;

namespace AetherFrame.Server.Tests;

/// <summary>
/// Publishing shared fairly once the server is open to everyone (the open alpha, October 1, 2026):
/// one upload slot per character and per address range, and an hour's image budget per character,
/// so no one player can hold the slots or the image worker.
/// </summary>
public class FairPublishTests
{
    private const long Aria = 12345678;

    [Fact]
    public void ACharacterOrAnAddress_HoldsOneSlotAtMost_AndTheTotalIsBounded()
    {
        var slots = new PublishSlots();
        var first = slots.TryTake(1, IPAddress.Parse("198.51.100.1"));
        Assert.NotNull(first);

        // The same character from elsewhere, or another character from the same address: refused.
        Assert.Null(slots.TryTake(1, IPAddress.Parse("198.51.100.2")));
        Assert.Null(slots.TryTake(2, IPAddress.Parse("198.51.100.1")));

        // An IPv6 /64 counts as one address.
        var v6 = slots.TryTake(3, IPAddress.Parse("2001:db8:1:2::10"));
        Assert.NotNull(v6);
        Assert.Null(slots.TryTake(4, IPAddress.Parse("2001:db8:1:2::99")));

        var rest = Enumerable.Range(0, PublishSlots.Total - 2).Select(index => slots.TryTake(10 + index, IPAddress.Parse("203.0.113." + (index + 1)))).ToList();
        Assert.All(rest, Assert.NotNull);
        Assert.Null(slots.TryTake(99, IPAddress.Parse("192.0.2.99")));

        // A released slot is free again, once.
        first!.Dispose();
        first.Dispose();
        var again = slots.TryTake(99, IPAddress.Parse("192.0.2.99"));
        Assert.NotNull(again);
        Assert.Null(slots.TryTake(98, IPAddress.Parse("192.0.2.98")));
    }

    [Fact]
    public async Task ACharacterWhoseImagesFailThreeTimes_WaitsTheHourOut()
    {
        using var server = new TestServer();
        using var aria = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        var png = Plates.Png(4, 3);

        // The worker fails on every image: each failure counts.
        server.Images.Answer = _ => null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var refused = await aria.PublishAsync(Plates.Snapshot(profile, "Plate " + attempt, png), png);
            Assert.Equal("image-refused", await refused.Content.ReadAsStringAsync());
        }

        server.Images.Answer = bytes => bytes;
        using var waiting = await aria.PublishAsync(Plates.Snapshot(profile, "Plate", png), png);
        Assert.Equal(HttpStatusCode.TooManyRequests, waiting.StatusCode);

        // A Plate without images still publishes.
        using var plain = await aria.PublishAsync(Plates.Snapshot(profile, "Plain"));
        Assert.Equal(HttpStatusCode.NoContent, plain.StatusCode);

        // An hour later the character may send images again.
        server.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        using var later = await aria.PublishAsync(Plates.Snapshot(profile, "Later", png), png);
        Assert.Equal(HttpStatusCode.NoContent, later.StatusCode);
    }
}
