using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using AetherFrame.Server.Limits;
using AetherFrame.Server.Lodestone;
using AetherFrame.Server.Requests;
using AetherFrame.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AetherFrame.Server.Tests;

/// <summary>The server's parts on their own: the page reader, names, codes, bodies, address groups and the database's settings.</summary>
public class ServerPartsTests
{
    private static readonly Worlds DefaultWorlds = new([]);

    [Fact]
    public void ThePageReader_TakesEachFieldFromItsOneElement()
    {
        var page = LodestoneHtml.Character("Aria Starfall", "Gilgamesh", "Hello AF-0123456789").Html!;
        var character = LodestonePage.Read(page, DefaultWorlds);
        Assert.Equal(new LodestoneCharacter("Aria Starfall", "Gilgamesh", "Hello AF-0123456789"), character);
    }

    [Theory]
    [InlineData("<p class=\"frame__chara__name\">Bram Oakes</p>")]
    [InlineData("<div class=\"character__selfintroduction\">AF-0123456789</div>")]
    [InlineData("<p class=\"frame__chara__world\">Balmung [Crystal]</p>")]
    public void ThePageReader_RefusesAPageWithTwoOfAnElement(string extra)
    {
        var page = LodestoneHtml.Character("Aria Starfall", "Gilgamesh", "x").Html!.Replace("</body>", extra + "</body>", StringComparison.Ordinal);
        Assert.Null(LodestonePage.Read(page, DefaultWorlds));
    }

    [Fact]
    public void ThePageReader_RefusesAPageMissingAnElement()
    {
        var page = LodestoneHtml.Character("Aria Starfall", "Gilgamesh", "x").Html!.Replace("character__selfintroduction", "character__profile", StringComparison.Ordinal);
        Assert.Null(LodestonePage.Read(page, DefaultWorlds));
        Assert.Null(LodestonePage.Read(LodestoneHtml.NotFoundPage, DefaultWorlds));
    }

    [Fact]
    public void TheNotFoundPage_IsRecognisedOnlyByItsOwnShape()
    {
        Assert.True(LodestonePage.IsNotFoundPage(LodestoneHtml.NotFoundPage));
        Assert.False(LodestonePage.IsNotFoundPage("<html><head><title>404 Not Found</title></head><body>nginx</body></html>"));
        Assert.False(LodestonePage.IsNotFoundPage(LodestoneHtml.Character("Aria Starfall", "Gilgamesh", "x").Html!));
    }

    [Theory]
    [InlineData("Aria Starfall", true)]
    [InlineData("A'ria Star-fall", true)]
    [InlineData("Ab Cd", true)]
    [InlineData("Abcdefghijklmno Abcde", true)]
    [InlineData("Abcdefghijklmnop Ab", false)]
    [InlineData("Abcdefghijklmno Abcdef", false)]
    [InlineData("A Starfall", false)]
    [InlineData("aria Starfall", false)]
    [InlineData("Aria  Starfall", false)]
    [InlineData("Aria", false)]
    [InlineData("Aria Star fall", false)]
    [InlineData("Aria Starfall ", false)]
    public void GameNames_FollowTheGamesRules(string name, bool allowed) => Assert.Equal(allowed, CharacterNames.IsGameName(name));

    [Theory]
    [InlineData("Aria Starfall", "aria starfall")]
    [InlineData("  ARIA   Starfall ", "aria starfall")]
    public void NameKeys_AreNfcLowerCaseWithSpacesFolded(string text, string key) => Assert.Equal(key, CharacterNames.Key(text));

    [Fact]
    public void TextOutsideAscii_IsJudgedByCodePoint()
    {
        var aAcute = char.ConvertFromUtf32(0xE1);
        var eAcute = char.ConvertFromUtf32(0xE9);
        var combiningAcute = char.ConvertFromUtf32(0x301);
        Assert.False(CharacterNames.IsGameName("Ari" + aAcute + " Starfall"));
        Assert.Equal("caf" + eAcute + " name", CharacterNames.Key("Cafe" + combiningAcute + " Name"));
        Assert.False(LodestoneIds.TryParse(char.ConvertFromUtf32(0x661) + char.ConvertFromUtf32(0x662), out _));
    }

    [Fact]
    public void Worlds_MatchWithoutRegardToCase_AndTheOperatorCanAddOne()
    {
        Assert.True(DefaultWorlds.TryFind("gILGAMESH", out var world));
        Assert.Equal("Gilgamesh", world);
        Assert.False(DefaultWorlds.TryFind("Atlantis", out _));
        Assert.False(DefaultWorlds.TryFind("Gilgamesh ", out _));
        Assert.True(new Worlds(["Atlantis"]).TryFind("atlantis", out _));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("9999999999", true)]
    [InlineData("0", false)]
    [InlineData("01", false)]
    [InlineData("12345678901", false)]
    [InlineData("-1", false)]
    [InlineData("1e3", false)]
    [InlineData("", false)]
    public void LodestoneIds_AreDigitsWithNoLeadingZero(string text, bool valid) => Assert.Equal(valid, LodestoneIds.TryParse(text, out _));

    [Fact]
    public void Codes_AreAFAndTenCrockfordSymbols()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => LodestoneCodes.NewCode()).ToList();
        Assert.All(codes, code => Assert.True(LodestoneCodes.IsWellFormed(code), code));
        Assert.True(codes.Distinct().Count() > 195);
        Assert.False(LodestoneCodes.IsWellFormed("AF-012345678I"));
        Assert.False(LodestoneCodes.IsWellFormed("AF-01234567"));
    }

    [Fact]
    public void ActionBodies_AreOneObjectWithExactlyTheNamedProperties()
    {
        static ActionBody? Read(string json) => ActionBody.Read(Encoding.UTF8.GetBytes(json), ["name", "world"], ["index"]);

        var body = Read("{\"world\":\"Gilgamesh\",\"name\":\"Aria Starfall\",\"index\":3}");
        Assert.NotNull(body);
        Assert.Equal("Aria Starfall", body.String("name"));
        Assert.Equal(3, body.Number("index"));
        Assert.Null(Read("{\"name\":\"a\",\"world\":\"b\"}"));
        Assert.Null(Read("{\"name\":\"a\",\"world\":\"b\",\"index\":\"3\"}"));
        Assert.Null(Read("{\"name\":\"a\",\"world\":\"b\",\"index\":3.5}"));
        Assert.Null(Read("{\"name\":\"a\",\"world\":\"b\",\"index\":3,\"name\":\"c\"}"));
        Assert.Null(Read("{\"name\":\"a\",\"world\":\"b\",\"index\":3,}"));
        Assert.Null(Read("{\"name\":\"a\",\"world\":[\"b\"],\"index\":3}"));
        Assert.Null(Read("{\"name\":\"a\",\"world\":\"b\",\"index\":3} x"));
        Assert.Null(Read("{\"Name\":\"a\",\"world\":\"b\",\"index\":3}"));
        Assert.Null(Read("null"));
        Assert.Null(ActionBody.Read([0xFF, 0xFE], []));
    }

    [Fact]
    public void AddressGroups_CountIPv6ByPrefix_AndMappedAddressesAsIPv4()
    {
        Assert.Equal([("4/192.0.2.7", 1)], AddressGroups.Of(IPAddress.Parse("192.0.2.7")));
        Assert.Equal([("4/192.0.2.7", 1)], AddressGroups.Of(IPAddress.Parse("::ffff:192.0.2.7")));
        var groups = AddressGroups.Of(IPAddress.Parse("2001:db8:1:2:3:4:5:6")).ToList();
        Assert.Equal(["6/64/20010DB800010002", "6/56/20010DB8000100", "6/48/20010DB80001"], groups.Select(g => g.Group));
        Assert.Equal([1, 4, 16], groups.Select(g => g.Multiple));
    }

    [Fact]
    public void TheLimiter_CountsEachGroup_AndForgetsAfterTheWindow()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var limiter = new RateLimiter(time);
        var limit = new Limit("test", 2, TimeSpan.FromHours(1));

        // Two /64s of one /56: each /64 has room for 2, the /56 for 8.
        Assert.True(limiter.TryTakeAddress(limit, IPAddress.Parse("2001:db8:0:1::1")));
        Assert.True(limiter.TryTakeAddress(limit, IPAddress.Parse("2001:db8:0:1::2")));
        Assert.False(limiter.TryTakeAddress(limit, IPAddress.Parse("2001:db8:0:1::3")));
        Assert.True(limiter.TryTakeAddress(limit, IPAddress.Parse("2001:db8:0:2::1")));

        time.Now += TimeSpan.FromHours(1);
        Assert.True(limiter.TryTakeAddress(limit, IPAddress.Parse("2001:db8:0:1::1")));
    }

    [Fact]
    public void TheLimiter_KeepsADaysCountThroughItsSweeps_AndDropsEmptyCounters()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var limiter = new RateLimiter(time);
        var daily = new Limit("daily", 2, TimeSpan.FromDays(1));
        var hourly = new Limit("hourly", 1, TimeSpan.FromHours(1));
        Assert.True(limiter.TryTake(daily, "id"));
        Assert.True(limiter.TryTake(daily, "id"));
        Assert.True(limiter.TryTake(hourly, "other"));

        // Hours later, sweeps have run: the daily count stands, and the hourly counter is gone.
        time.Now += TimeSpan.FromHours(20);
        Assert.False(limiter.TryTake(daily, "id"));
        time.Now += TimeSpan.FromMinutes(2);
        Assert.False(limiter.TryTake(daily, "id"));
        Assert.Equal(1, limiter.Count);

        time.Now += TimeSpan.FromHours(4);
        Assert.True(limiter.TryTake(daily, "id"));
    }

    [Fact]
    public async Task EveryPooledConnection_HasSecureDeleteOn()
    {
        using var server = new TestServer();
        using var client = server.CreateClient();
        var database = server.Services.GetRequiredService<ServerDatabase>();
        for (var index = 0; index < 3; index++)
        {
            await using var connection = await database.OpenAsync(default);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA secure_delete;";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
            command.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", await command.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task ATestServersTeardown_ClosesItsOwnPooledConnections_AndNoOtherServers()
    {
        // Known bug 12: the teardown cleared every pool in the process, under the servers of the test
        // classes running beside it.
        using var other = new TestServer();
        using var otherClient = other.CreateClient();
        SQLitePCL.sqlite3 othersConnection;
        await using (var connection = await other.Services.GetRequiredService<ServerDatabase>().OpenAsync(default))
        {
            othersConnection = connection.Handle!;
        }

        SQLitePCL.sqlite3 ownConnection;
        using (var server = new TestServer())
        {
            using var client = server.CreateClient();
            await using var connection = await server.Services.GetRequiredService<ServerDatabase>().OpenAsync(default);
            ownConnection = connection.Handle!;
        }

        // Its own, back in its pool, is closed, so that its folder can be deleted; the other's is still open.
        Assert.True(ownConnection.IsClosed);
        Assert.False(othersConnection.IsClosed);
    }

    [Fact]
    public async Task TheCheckpoint_RetriesWhileAReaderBlocksIt()
    {
        using var server = new TestServer();
        using var client = server.CreateClient();
        var database = server.Services.GetRequiredService<ServerDatabase>();
        await using (var writer = await database.OpenAsync(default))
        {
            await ServerDatabase.ExecuteAsync(writer, "INSERT INTO reports (lodestone_id, reason, reporter, day) VALUES (1, 'other', 'x', 0);", default);
        }

        // A reader holding a read transaction blocks TRUNCATE until it ends.
        var reader = await database.OpenAsync(default);
        await ServerDatabase.ExecuteAsync(reader, "BEGIN; SELECT COUNT(*) FROM reports;", default);
        await using (var writer = await database.OpenAsync(default))
        {
            await ServerDatabase.ExecuteAsync(writer, "DELETE FROM reports;", default);
        }

        var checkpoint = database.CheckpointAsync(default);
        await Task.Delay(200);
        Assert.False(checkpoint.IsCompleted);
        await ServerDatabase.ExecuteAsync(reader, "COMMIT;", default);
        await reader.DisposeAsync();
        Assert.True(await checkpoint.WaitAsync(TimeSpan.FromSeconds(20)));
    }
}
