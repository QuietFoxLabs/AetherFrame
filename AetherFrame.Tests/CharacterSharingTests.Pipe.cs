using System;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Services.Network.Sharing;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// The check and the re-read through the player's own connection ("Checking a character through
/// the player's own connection" in the decision register; ServerApi-v1.md, section 2.3): sent as
/// WebSockets, the Lodestone's refusal explained, and re-reads only when due, timed by the day of
/// the last read the server's answers carry.
/// </summary>
public partial class CharacterSharingTests
{
    [Fact]
    public void TheCheck_GoesAsAWebSocket_NamingOnlyTheVersion()
    {
        using var harness = new SharingHarness();
        harness.Bound();

        var upgrade = Assert.Single(harness.Server.Upgrades);
        Assert.Equal(new SeenUpgrade("/v1/lodestone/check", "AetherFrame/0.1.7", Origin: false), upgrade);
        Assert.Empty(harness.Server.Faults);
    }

    [Fact]
    public void TheCheck_CarriesTheLodestonesBytes_WhenTheServerOpensThePipe()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        var hello = TlsBytes.ClientHello();
        byte[] page = [0x17, 0x03, 0x03, 0x00, 0x02, 9, 9];
        harness.Server.Pipe = async socket =>
        {
            await WebSocketStandIn.SendTextAsync(socket, "open");
            await WebSocketStandIn.ExpectTextAsync(socket, "opened");
            var link = Assert.Single(harness.Links);
            await WebSocketStandIn.SendBinaryAsync(socket, hello);
            Assert.True(await link.Arrived.WaitAsync(Patience));
            link.Answer(page);
            var (type, bytes) = await WebSocketStandIn.ReceiveAsync(socket);
            Assert.Equal(WebSocketMessageType.Binary, type);
            Assert.Equal(page, bytes);
            link.End();
            await WebSocketStandIn.ExpectTextAsync(socket, "eof");
            await WebSocketStandIn.SendTextAsync(socket, "close");
        };

        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");

        Assert.Empty(harness.Server.Faults);
        Assert.Equal(SharingStage.Shared, harness.Sharing.View.Find(Aria)!.Stage);
        var used = Assert.Single(harness.Links);
        Assert.Equal(hello, used.Received);
        Assert.True(used.Disposed);
    }

    [Fact]
    public void AConnectionToAnAddressOffTheAllowedList_IsRefused_AndTheServerTold()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.NextLink = () => new FakeLodestoneLink(IPAddress.Parse("192.168.1.20"));
        harness.Server.Pipe = async socket =>
        {
            await WebSocketStandIn.SendTextAsync(socket, "open");
            await WebSocketStandIn.ExpectTextAsync(socket, "failed");
            await WebSocketStandIn.SendTextAsync(socket, "close");
        };
        harness.Server.Answers["/v1/lodestone/check"] = _ => (HttpStatusCode.ServiceUnavailable, null);

        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");

        Assert.Empty(harness.Server.Faults);
        Assert.Equal(SharingNoticeKind.TryLater, harness.Sharing.View.Notice!.Kind);
        var link = Assert.Single(harness.Links);
        Assert.Empty(link.Received);
        Assert.True(link.Disposed);
    }

    [Fact]
    public void TheLodestoneTurningTheConnectionAway_IsExplained()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Server.Finals["/v1/lodestone/check"] = _ => "{\"status\":503,\"reason\":\"lodestone:refused\"}";

        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");

        Assert.Equal(SharingStage.Checking, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Equal(new SharingNotice(Aria, SharingNoticeKind.LodestoneRefused), harness.Sharing.View.Notice);
        Assert.True(SharingText.IsProblem(SharingNoticeKind.LodestoneRefused));
        Assert.Contains("another connection", SharingText.Notice(SharingNoticeKind.LodestoneRefused), StringComparison.Ordinal);
        Assert.Contains(harness.Log, line => line.Contains("the Lodestone refused the connection", StringComparison.Ordinal));
    }

    [Fact]
    public void ARefusedChallenge_IsSignedAgain_OnANewWebSocket()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Server.Finals["/v1/lodestone/check"] = _ =>
        {
            harness.Server.Finals.Remove("/v1/lodestone/check");
            return "{\"status\":409,\"challenge\":\"" + harness.Server.FreshChallenge() + "\"}";
        };

        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");

        Assert.Equal(SharingStage.Shared, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Equal(2, harness.Server.Upgrades.Count);
        Assert.Equal(2, harness.Server.Actions.Count(action => action.Path == "/v1/lodestone/check"));
    }

    [Fact]
    public void NoFinalMessage_IsNoAnswer()
    {
        using var harness = new SharingHarness();
        harness.Sharing.TryStart(Aria, newKey: false);
        harness.Server.Pipe = socket =>
        {
            socket.Abort();
            throw new OperationCanceledException("dropped, as the server drops a session it ends");
        };

        harness.Sharing.TryCheck(Aria, "12345678", "Aria Starfall", "Gilgamesh");

        Assert.Equal(SharingStage.Checking, harness.Sharing.View.Find(Aria)!.Stage);
        Assert.Equal(SharingNoticeKind.Unreachable, harness.Sharing.View.Notice!.Kind);
    }

    [Fact]
    public void AReread_IsDue_WhenTheLastReadIsFifteenDaysOld_AndNotBefore()
    {
        using var harness = new SharingHarness();
        var today = CharacterSharing.DayOf(harness.Now);
        harness.Server.ReadDay = today - (CharacterSharing.RereadAfterDays - 1);
        harness.Bound();
        Assert.False(harness.Sharing.RereadDue(Aria, "Aria Starfall", "Gilgamesh"));

        harness.Now = harness.Now.AddDays(1);
        Assert.True(harness.Sharing.RereadDue(Aria, "Aria Starfall", "Gilgamesh"));

        // Read again, and found as it was: no request changes the binding, and nothing is said.
        harness.Server.ReadDay = CharacterSharing.DayOf(harness.Now);
        var file = harness.File.Read();
        Assert.True(harness.Sharing.TryReread(Aria, "Aria Starfall", "Gilgamesh"));
        Assert.Equal("/v1/lodestone/reread", harness.Server.Upgrades.Last().Path);
        Assert.Equal(file, harness.File.Read());
        Assert.Equal(SharingNoticeKind.CheckPassed, harness.Sharing.View.Notice!.Kind);
        Assert.False(harness.Sharing.RereadDue(Aria, "Aria Starfall", "Gilgamesh"));
    }

    [Fact]
    public void AReread_IsDueOnceASession_WhenNoReadIsKnownYet_AndNeverWithoutABinding()
    {
        using var harness = new SharingHarness();
        Assert.False(harness.Sharing.RereadDue(Aria, "Aria Starfall", "Gilgamesh"));
        harness.Bound();
        Assert.False(harness.Sharing.RereadDue(Aria, "Aria Starfall", "Gilgamesh"));

        harness.Restart();
        Assert.True(harness.Sharing.RereadDue(Aria, "Aria Starfall", "Gilgamesh"));
        harness.Sharing.TryReread(Aria, "Aria Starfall", "Gilgamesh");
        Assert.False(harness.Sharing.RereadDue(Aria, "Aria Starfall", "Gilgamesh"));
        Assert.Single(harness.Server.Upgrades, upgrade => upgrade.Path == "/v1/lodestone/reread");
    }

    [Fact]
    public void AReread_ThatGotNoAnswer_IsntAskedForAgainWithinTheHour()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        harness.Server.Finals["/v1/lodestone/reread"] = _ => "{\"status\":503,\"reason\":\"lodestone:refused\"}";

        harness.Sharing.TryReread(Aria, "Aria Moonfall", "Gilgamesh");
        Assert.Equal(new SharingNotice(Aria, SharingNoticeKind.LodestoneRefused), harness.Sharing.View.Notice);
        Assert.False(harness.Sharing.RereadDue(Aria, "Aria Moonfall", "Gilgamesh"));
        harness.Sharing.TryReread(Aria, "Aria Moonfall", "Gilgamesh");
        Assert.Single(harness.Server.Upgrades, upgrade => upgrade.Path == "/v1/lodestone/reread");

        harness.Now = harness.Now.AddMinutes(61);
        Assert.True(harness.Sharing.RereadDue(Aria, "Aria Moonfall", "Gilgamesh"));
        Assert.Equal("Aria Starfall", harness.Sharing.View.Find(Aria)!.Name);
    }

    [Fact]
    public void ARereadAnsweredWithTheOldName_IsntAskedForAgainThisSession_UntilTheGameShowsAnother()
    {
        // The Lodestone can take a while to show a rename: the page still says Aria Starfall.
        using var harness = new SharingHarness();
        harness.Bound();
        harness.Server.Answers["/v1/lodestone/reread"] = _ => (HttpStatusCode.OK, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}");
        Assert.True(harness.Sharing.RereadDue(Aria, "Aria Moonfall", "Gilgamesh"));
        Assert.True(harness.Sharing.TryReread(Aria, "Aria Moonfall", "Gilgamesh"));
        Assert.Equal("Aria Starfall", harness.Sharing.View.Find(Aria)!.Name);

        // Not within the hour, answered or not; and after it, not for the same name and World.
        Assert.False(harness.Sharing.RereadDue(Aria, "Aria Moonfall", "Gilgamesh"));
        harness.Now = harness.Now.AddHours(2);
        Assert.False(harness.Sharing.RereadDue(Aria, "Aria Moonfall", "Gilgamesh"));
        Assert.True(harness.Sharing.RereadDue(Aria, "Aria Skyfall", "Gilgamesh"));
        Assert.True(harness.Sharing.RereadDue(Aria, "Aria Moonfall", "Cactuar"));

        // The fifteen days still apply.
        harness.Now = harness.Now.AddDays(CharacterSharing.RereadAfterDays);
        Assert.True(harness.Sharing.RereadDue(Aria, "Aria Moonfall", "Gilgamesh"));
        Assert.Single(harness.Server.Upgrades, upgrade => upgrade.Path == "/v1/lodestone/reread");
    }

    [Fact]
    public void NoRereadIsDue_BeforeThePlayerHasSeenTheOneTimeNotice()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        System.IO.File.Delete(System.IO.Path.Combine(harness.Root, CharacterSharing.ConnectionNoticeFileName));
        harness.Restart();
        Assert.True(harness.Sharing.View.ConnectionNotice);

        Assert.False(harness.Sharing.RereadDue(Aria, "Aria Moonfall", "Gilgamesh"));
        Assert.True(harness.Sharing.TryReread(Aria, "Aria Moonfall", "Gilgamesh"));
        harness.WaitIdle();
        Assert.DoesNotContain(harness.Server.Upgrades, upgrade => upgrade.Path == "/v1/lodestone/reread");

        Assert.True(harness.Sharing.TryDismissConnectionNotice());
        Assert.True(harness.Sharing.RereadDue(Aria, "Aria Moonfall", "Gilgamesh"));
    }

    [Fact]
    public void TheOneTimeNotice_ShowsOnlyForCharactersSharedBefore_AndOnlyUntilDismissed()
    {
        using var harness = new SharingHarness();
        Assert.False(harness.Sharing.View.ConnectionNotice);

        // A check passed under the new consent needs no notice, now or later.
        harness.Bound();
        Assert.False(harness.Sharing.View.ConnectionNotice);
        harness.Restart();
        Assert.False(harness.Sharing.View.ConnectionNotice);

        // A character shared before this build, as its marker's absence shows.
        System.IO.File.Delete(System.IO.Path.Combine(harness.Root, CharacterSharing.ConnectionNoticeFileName));
        harness.Restart();
        Assert.True(harness.Sharing.View.ConnectionNotice);
        Assert.True(harness.Sharing.TryDismissConnectionNotice());
        Assert.False(harness.Sharing.View.ConnectionNotice);
        harness.Restart();
        Assert.False(harness.Sharing.View.ConnectionNotice);
    }

    [Fact]
    public void TheConsent_AndTheNotice_SayWhatTheLodestoneSees_AndTheThirtyDays()
    {
        Assert.Contains(SharingText.ReadsThroughYourConnection, SharingText.Consent);
        Assert.Contains(SharingText.ThirtyDays, SharingText.Consent);
        Assert.Contains(SharingText.ReadsThroughYourConnection, SharingText.ConnectionNotice);
        Assert.Contains(SharingText.ThirtyDays, SharingText.ConnectionNotice);
        Assert.Contains("network address", SharingText.ReadsThroughYourConnection, StringComparison.Ordinal);
        Assert.Contains(SharingText.Consent, line => line.Contains("the day its Lodestone page was last read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LookingUpAPlate_AsksForADueRereadFirst()
    {
        using var harness = new SharingHarness();
        harness.Bound();
        harness.Restart();
        var asked = 0;
        var viewing = new PlateViewing(
            (_, _) => false,
            () => harness.Sharing.View,
            () => Aria,
            harness.Client,
            new HiddenPlates(harness.Root, harness.Log.Add),
            (_, _) => { },
            CancellationToken.None,
            harness.Log.Add)
        {
            Looking = () =>
            {
                asked++;
                if (harness.Sharing.RereadDue(Aria, "Aria Starfall", "Gilgamesh"))
                {
                    harness.Sharing.TryReread(Aria, "Aria Starfall", "Gilgamesh");
                }
            },
        };

        Assert.True(viewing.Open("Bram Oakes", "Gilgamesh"));
        await Task.Yield();
        Assert.Equal(1, asked);
        Assert.Single(harness.Server.Upgrades, upgrade => upgrade.Path == "/v1/lodestone/reread");
    }
}
