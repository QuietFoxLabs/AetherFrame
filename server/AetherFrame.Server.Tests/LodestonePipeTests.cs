using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Documents;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Requests;
using AetherFrame.Protocol.Signing;
using AetherFrame.Server.Endpoints;
using AetherFrame.Server.Hosting;
using AetherFrame.Server.Limits;
using AetherFrame.Server.Lodestone;
using AetherFrame.Server.Requests;
using AetherFrame.Server.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AetherFrame.Server.Tests;

/// <summary>
/// The Lodestone check and re-read as WebSockets, read through the player's own connection
/// (ServerApi-v1.md, section 2.3; "Checking a character through the player's own connection" in the
/// decision register), end to end: the test host's WebSocket client plays the plugin, and pipes to a
/// TLS stand-in for the Lodestone on loopback whose certificate the server trusts only through
/// <see cref="PipedPages.TrustForTests"/>. Nothing here reaches the real Lodestone.
/// </summary>
public sealed class LodestonePipeTests
{
    private const long Aria = 12345678;
    private const long Bram = 23456789;
    private const long OffTheAllowlist = 45678901;
    private const string CheckPath = "/v1/lodestone/check";
    private const string RereadPath = "/v1/lodestone/reread";

    [Fact]
    public async Task APipedCheck_BindsTheCharacter_ThroughThePlayersOwnConnection()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone();
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();
        lodestone.Answer = _ => TlsLodestone.Chunked(200, Page("Aria Starfall", "Gilgamesh", "Hello! " + code));

        var run = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));

        Assert.Equal(200, run.Status);
        Assert.Equal(["open", "close"], run.Texts);
        Assert.True(ProfileId.TryParse(run.Body.GetProperty("profileId").GetString(), out _));
        Assert.Equal("Aria Starfall", run.Body.GetProperty("name").GetString());
        Assert.Equal("Gilgamesh", run.Body.GetProperty("world").GetString());
        Assert.Equal(1L, await server.CountAsync("SELECT COUNT(*) FROM bindings;"));

        // C2's fetch, exactly: the fixed address over HTTP/1.1, the server's User-Agent, and nothing
        // that asks for compression or carries a trace, a cookie or a credential.
        var request = Assert.Single(lodestone.Requests);
        Assert.StartsWith("GET /lodestone/character/12345678/ HTTP/1.1\r\n", request, StringComparison.Ordinal);
        Assert.Contains("\r\nHost: na.finalfantasyxiv.com\r\n", request, StringComparison.Ordinal);
        Assert.Contains("\r\nUser-Agent: AetherFrame-Server/1 (+https://plates.example.com/)\r\n", request, StringComparison.Ordinal);
        foreach (var header in new[] { "Accept-Encoding", "traceparent", "Request-Id", "Cookie", "Authorization", "Proxy-" })
        {
            Assert.DoesNotContain(header, request, StringComparison.OrdinalIgnoreCase);
        }

        // Nothing went through the server's own client, the relay's.
        Assert.Empty(server.Lodestone.Fetched);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task APipedReread_FollowsARename_AndRecordsTheDayOfTheRead()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria, "Aria Starfall", "Gilgamesh");
        server.Time.Advance(TimeSpan.FromDays(20));
        lodestone.Answer = _ => TlsLodestone.Framed(200, Page("Aria Brightwater", "Balmung", "", "Crystal"));

        var run = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, RereadPath, await player.RereadBodyAsync());

        Assert.Equal(200, run.Status);
        Assert.Equal(["open", "close"], run.Texts);
        Assert.Equal("Aria Brightwater", run.Body.GetProperty("name").GetString());
        Assert.Equal("Balmung", run.Body.GetProperty("world").GetString());
        Assert.Equal(Today(server), await server.CountAsync("SELECT read_day FROM bindings;"));
        Assert.StartsWith("GET /lodestone/character/12345678/ HTTP/1.1\r\n", Assert.Single(lodestone.Requests), StringComparison.Ordinal);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task ThePaths_StillAnswerAPostThroughTheServersOwnClient_AndAGetThatIsntAWebSocketIs400()
    {
        using var server = NewServer();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria);
        Assert.Equal([Aria], server.Lodestone.Fetched);
        using (var reread = await player.SendAsync(RereadPath, RequestProofKind.LodestoneReread, "{}"))
        {
            Assert.Equal(HttpStatusCode.OK, reread.StatusCode);
            Assert.Equal("application/json; charset=utf-8", reread.Content.Headers.ContentType?.ToString());
        }

        Assert.Equal([Aria, Aria], server.Lodestone.Fetched);
        foreach (var path in new[] { CheckPath, RereadPath })
        {
            using var get = await player.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
            Assert.Empty(await get.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task APipeAnsweringWithItsOwnCertificate_GetsNoRequest_AndTheCheckIsTryAgainLater()
    {
        // What a player forging a page would have to do: answer the TLS itself, for the Lodestone's
        // name, with a certificate no trusted authority signed. The server refuses it, so no request
        // reaches it, and nothing binds.
        await AssertNoRequestReachesAsync(TestAuthority.OwnCertificate);
    }

    [Fact]
    public async Task APipeAnsweringWithACertificateForAnotherName_GetsNoRequest()
    {
        await AssertNoRequestReachesAsync(TestAuthority.OtherName);
    }

    [Fact]
    public async Task APipeOfferingOnlyTls12_GetsNoRequest()
    {
        await AssertNoRequestReachesAsync(TestAuthority.Lodestone, SslProtocols.Tls12);
    }

    [Fact]
    public async Task ThePipesHandler_HandsOutItsOnePipeOnce_ForTheLodestoneAlone()
    {
        using var server = new TestServer();
        var pages = server.Services.GetRequiredService<PipedPages>();
        using var pipe = new MemoryStream();
        using var handler = pages.CreateHandler(pipe);
        Assert.Equal(1, handler.MaxConnectionsPerServer);
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(0, handler.MaxResponseDrainSize);
        Assert.Null(handler.ActivityHeadersPropagator);
        Assert.Equal(SslProtocols.Tls13, handler.SslOptions.EnabledSslProtocols);

        // The certificate is checked as .NET checks any: no callback, and, outside tests, no trust store of its own.
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(handler.SslOptions.CertificateChainPolicy);

        // The first connection is the pipe (TLS then fails on its empty stream); a second is refused.
        using var client = new HttpClient(handler, disposeHandler: false);
        var first = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(LodestoneHttpPages.AddressOf(Aria)));
        Assert.IsNotType<InvalidOperationException>(first.InnerException);
        var second = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(LodestoneHttpPages.AddressOf(Aria)));
        Assert.IsType<InvalidOperationException>(second.InnerException);

        // Nor is any other host handed the pipe.
        using var elsewhere = new HttpClient(pages.CreateHandler(new MemoryStream()));
        var other = await Assert.ThrowsAsync<HttpRequestException>(() => elsewhere.GetAsync(new Uri("https://eu.finalfantasyxiv.com/lodestone/character/12345678/")));
        Assert.IsType<InvalidOperationException>(other.InnerException);
    }

    [Fact]
    public async Task AResponseEndedOnlyByTheClose_IsRefused_AndNeverCountsAsNotFound()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone { CloseAfterAnswer = true };
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();
        lodestone.Answer = _ => TlsLodestone.Unframed(200, Page("Aria Starfall", "Gilgamesh", code));

        var check = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));
        Assert.Equal(503, check.Status);
        Assert.Null(check.Reason);
        Assert.True(lodestone.SawRequest);
        Assert.Equal(0L, await server.CountAsync("SELECT COUNT(*) FROM bindings;"));

        // The Lodestone's own "not found" page, ended only by the close: not a "not found".
        await player.BindAsync(Aria);
        lodestone.Answer = _ => TlsLodestone.Unframed(404, LodestoneHtml.NotFoundPage);
        var reread = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, RereadPath, await player.RereadBodyAsync());
        Assert.Equal(503, reread.Status);
        Assert.Equal(0L, await server.CountAsync("SELECT COUNT(*) FROM bindings WHERE not_found_day IS NOT NULL;"));
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task ADroppedOrCutOffPipe_NeverCountsAsNotFound_TheLodestonesOwnPageDoes()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria);
        async Task<PipeRun> RereadAsync(PluginPipe plugin) => await plugin.RunAsync(server, RereadPath, await player.RereadBodyAsync());

        // The plugin can't connect.
        Assert.Equal(503, (await RereadAsync(new PluginPipe { OnOpen = OpenAnswer.Fail })).Status);

        // The page is cut off: the connection closes before its length is read.
        await using (var cutOff = new TlsLodestone { CloseAfterAnswer = true, Answer = _ => TlsLodestone.CutOff(404, LodestoneHtml.NotFoundPage) })
        {
            Assert.Equal(503, (await RereadAsync(new PluginPipe { Lodestone = cutOff.EndPoint })).Status);
        }

        // The Lodestone's own page, framed by its length but not trusted: no answer at all.
        await using (var forged = new TlsLodestone(TestAuthority.OwnCertificate))
        {
            Assert.Equal(503, (await RereadAsync(new PluginPipe { Lodestone = forged.EndPoint })).Status);
        }

        Assert.Equal(0L, await server.CountAsync("SELECT COUNT(*) FROM bindings WHERE not_found_day IS NOT NULL;"));

        // The Lodestone's own "not found" page, with a 404 and framed, counts: the binding stays until a second a day apart.
        lodestone.Answer = _ => TlsLodestone.Framed(404, LodestoneHtml.NotFoundPage);
        var found = await RereadAsync(new PluginPipe { Lodestone = lodestone.EndPoint });
        Assert.Equal(200, found.Status);
        Assert.Equal(1L, await server.CountAsync("SELECT COUNT(*) FROM bindings WHERE not_found_day IS NOT NULL;"));
        await AssertReleasedAsync(server);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    public async Task TheLodestoneTurningThePlayersConnectionAway_Is503_WithItsOwnReason(int status)
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone { Answer = _ => TlsLodestone.Framed(status, "<html><body>Request blocked.</body></html>") };
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();

        var check = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));
        Assert.Equal(503, check.Status);
        Assert.Equal(LodestoneActions.RefusedReason, check.Reason);
        Assert.Equal(["open", "close"], check.Texts);

        await player.BindAsync(Aria);
        var reread = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, RereadPath, await player.RereadBodyAsync());
        Assert.Equal(503, reread.Status);
        Assert.Equal("lodestone:refused", reread.Reason);
        Assert.Equal(0L, await server.CountAsync("SELECT COUNT(*) FROM bindings WHERE not_found_day IS NOT NULL;"));
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task FailedFromThePlugin_IsTryAgainLater()
    {
        using var server = NewServer();
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();

        var run = await new PluginPipe { OnOpen = OpenAnswer.Fail }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));

        Assert.Equal(503, run.Status);
        Assert.Null(run.Reason);
        Assert.Equal(["open", "close"], run.Texts);
        Assert.Equal(0L, await server.CountAsync("SELECT COUNT(*) FROM bindings;"));
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task NoOpenedInTime_IsTryAgainLater()
    {
        using var server = NewServer();
        server.Services.GetRequiredService<LodestoneSockets>().OpenedDeadline = TimeSpan.FromMilliseconds(300);
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();

        var run = await new PluginPipe { OnOpen = OpenAnswer.Ignore }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));

        Assert.Equal(503, run.Status);
        Assert.Equal(["open", "close"], run.Texts);
        Assert.True(run.CloseAt >= TimeSpan.FromMilliseconds(250), $"close came at {run.CloseAt}");
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task AFetchPastItsDeadline_IsTryAgainLater()
    {
        using var server = NewServer();
        // Long enough for the TLS handshake and the request on a loaded runner: the request must arrive.
        server.Services.GetRequiredService<LodestoneSockets>().FetchDeadline = TimeSpan.FromSeconds(5);
        await using var lodestone = new TlsLodestone { Stall = true };
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();

        var run = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));

        Assert.Equal(503, run.Status);
        Assert.True(lodestone.SawRequest);
        Assert.Equal(["open", "close"], run.Texts);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task TheSessionsDeadline_EndsIt_WithNoFinalMessage()
    {
        using var server = NewServer();
        var sockets = server.Services.GetRequiredService<LodestoneSockets>();
        sockets.SessionDeadline = TimeSpan.FromSeconds(2);
        sockets.OpenedDeadline = TimeSpan.FromSeconds(30);
        sockets.FetchDeadline = TimeSpan.FromSeconds(30);
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();

        var run = await new PluginPipe { OnOpen = OpenAnswer.Ignore }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));

        Assert.True(run.Cut);
        Assert.Null(run.Final);
        Assert.Equal(["open"], run.Texts);
        await AssertReleasedAsync(server);
        await AssertLoggedAsync(server, "socket:cut:deadline");
    }

    [Fact]
    public async Task AFirstMessageThatComesLate_EndsTheSession()
    {
        using var server = NewServer();
        server.Services.GetRequiredService<LodestoneSockets>().FirstMessageDeadline = TimeSpan.FromMilliseconds(300);
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();

        var run = await new PluginPipe { FirstMessageDelay = TimeSpan.FromSeconds(1) }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));

        Assert.True(run.Cut);
        Assert.Empty(run.Texts);
        await AssertReleasedAsync(server);
        await AssertLoggedAsync(server, "socket:cut:first-message-late");
    }

    [Fact]
    public async Task AFirstMessageOverTheSignedBodysBound_OrNotBinary_EndsTheSession()
    {
        using var server = NewServer();
        Assert.True((await new PluginPipe().RunAsync(server, CheckPath, new byte[SignedRequests.MaxActionRequestBytes + 1])).Cut);

        // At the bound it is read, and answered as a POST of it would be.
        Assert.Equal(400, (await new PluginPipe().RunAsync(server, CheckPath, new byte[SignedRequests.MaxActionRequestBytes])).Status);

        var client = server.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.HttpContext.Connection.RemoteIpAddress = PluginPipe.NextAddress();
        using var socket = await client.ConnectAsync(new Uri("ws://localhost" + CheckPath), CancellationToken.None);
        await PluginPipe.SendTextAsync(socket, "{}");
        await AssertDroppedAsync(socket);
        await AssertReleasedAsync(server);
        await AssertLoggedAsync(server, "socket:cut:first-message-size");
        await AssertLoggedAsync(server, "socket:cut:first-message-type");
    }

    [Fact]
    public async Task AMessageBeforeOpen_EndsTheSession()
    {
        using var server = NewServer();
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();

        var run = await new PluginPipe
        {
            AfterFirstMessage = socket => socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None),
        }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));

        Assert.True(run.Cut);
        Assert.Equal(0L, await server.CountAsync("SELECT COUNT(*) FROM bindings;"));
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task BytesInPlaceOfAnAnswerToOpen_EndTheSession()
    {
        using var server = NewServer();
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();

        var run = await new PluginPipe { OnOpen = OpenAnswer.Bytes }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));

        Assert.True(run.Cut);
        Assert.Equal(["open"], run.Texts);
        await AssertReleasedAsync(server);
        await AssertLoggedAsync(server, "socket:cut:unexpected-bytes");
    }

    [Fact]
    public async Task ASecondAnswerToOpen_EndsTheSession()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone();
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();
        lodestone.Answer = _ => TlsLodestone.Framed(200, Page("Aria Starfall", "Gilgamesh", code));

        var run = await new PluginPipe { Lodestone = lodestone.EndPoint, OnOpen = OpenAnswer.OpenedTwice }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));

        Assert.True(run.Cut);
        Assert.Equal(0L, await server.CountAsync("SELECT COUNT(*) FROM bindings;"));
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task AnUnknownTextMessage_EndsTheSession()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone { Stall = true };
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();

        var run = await new PluginPipe
        {
            Lodestone = lodestone.EndPoint,
            AfterOpened = socket => PluginPipe.SendTextAsync(socket, "Opened"),
        }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));

        Assert.True(run.Cut);
        await AssertReleasedAsync(server);
        await AssertLoggedAsync(server, "socket:cut:unexpected-message");
    }

    [Fact]
    public async Task AMessageOver64KiB_EndsTheSession()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone { Stall = true };
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();

        var run = await new PluginPipe
        {
            Lodestone = lodestone.EndPoint,
            AfterOpened = socket => socket.SendAsync(new byte[LodestoneSockets.MaxMessageBytes + 1], WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None),
        }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));

        Assert.True(run.Cut);
        await AssertReleasedAsync(server);
        await AssertLoggedAsync(server, "socket:cut:message-size");
    }

    [Fact]
    public async Task WhatWasInFlightAtClose_IsDropped_ButAnythingElseEndsTheSession()
    {
        // The delay after the read is the window in which the stray "opened" below must arrive.
        using var server = NewServer(afterRead: TimeSpan.FromSeconds(2));
        await using var lodestone = new TlsLodestone();
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();
        lodestone.Answer = _ => TlsLodestone.Framed(200, Page("Aria Starfall", "Gilgamesh", "no code here"));

        // Bytes and an eof that were on their way when close came: the failed check still answers.
        var inFlight = await new PluginPipe
        {
            Lodestone = lodestone.EndPoint,
            AfterClose = async socket =>
            {
                await socket.SendAsync(new byte[] { 0x17, 0x03, 0x03 }, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
                await PluginPipe.SendTextAsync(socket, "eof");
            },
        }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));
        Assert.Equal(422, inFlight.Status);

        // A second "opened" after close was never on its way: the session ends before its answer.
        var stray = await new PluginPipe
        {
            Lodestone = lodestone.EndPoint,
            AfterClose = socket => PluginPipe.SendTextAsync(socket, "opened"),
        }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));
        Assert.True(stray.Cut);
        await AssertReleasedAsync(server);
        await AssertLoggedAsync(server, "socket:cut:unexpected-message");
    }

    [Fact]
    public async Task BytesFromTheLodestonePastTheirTotal_EndTheSession()
    {
        // The stand-in answers 3 MiB, and the plugin keeps forwarding after "close". The read stops
        // at the page's 1 MiB, and the bytes still count after "close": past 2 MiB the session ends,
        // before the failed check's answer is due.
        using var server = NewServer(afterRead: TimeSpan.FromSeconds(3));
        await using var lodestone = new TlsLodestone { Answer = _ => TlsLodestone.Chunked(200, new byte[3 * 1024 * 1024]) };
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();

        var run = await new PluginPipe { Lodestone = lodestone.EndPoint, KeepForwardingAfterClose = true }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));

        Assert.True(run.Cut);
        Assert.Equal(0L, await server.CountAsync("SELECT COUNT(*) FROM bindings;"));
        await AssertReleasedAsync(server);
        await AssertLoggedAsync(server, "socket:cut:bytes-from-lodestone");
    }

    [Fact]
    public async Task TheServersBytesTowardTheLodestone_StopAtTheirTotal()
    {
        // The server's own request is far smaller, so this drives one session over .NET's own
        // WebSockets, as Kestrel and the plugin use them, with a fetch that writes too much.
        using var server = new TestServer();
        using var pair = await SocketPair.OpenAsync();
        var pages = new WritingPages(LodestoneSockets.MaxBytesToLodestone);
        using var session = new PipeSession(pair.Server, server.Services.GetRequiredService<LodestoneSockets>(), server.Services.GetRequiredService<PipedReads>(), pages);
        var running = session.RunAsync(
            async (body, cancellation) => ActionAnswer.Fail(503, "test:" + (await session.ReadAsync(Aria, cancellation)).Outcome),
            CancellationToken.None);

        var received = new List<int>();
        var run = await ExchangeCountingAsync(pair.Plugin, received);

        Assert.Equal("socket:503:test:Unanswered", await running);
        Assert.True(pages.Refused);
        Assert.Equal(LodestoneSockets.MaxBytesToLodestone, received.Sum());
        Assert.Equal(503, run.Status);
    }

    [Fact]
    public async Task AWholeExchange_OverDotNetsOwnWebSockets_ReadsThePage_AndOpensOnePipeAtMost()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone { Answer = _ => TlsLodestone.Framed(200, Page("Aria Starfall", "Gilgamesh", "")) };
        using var pair = await SocketPair.OpenAsync();
        using var session = new PipeSession(pair.Server, server.Services.GetRequiredService<LodestoneSockets>(), server.Services.GetRequiredService<PipedReads>(), server.Services.GetRequiredService<IPipedPages>());
        var running = session.RunAsync(
            async (body, cancellation) =>
            {
                var read = await session.ReadAsync(Aria, cancellation);
                await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadAsync(Aria, cancellation));
                return read is { Outcome: LodestoneOutcome.Found }
                    ? ActionAnswer.Ok(new CharacterEndpoints.RereadAnswer(read.Character!.Name, read.Character.World))
                    : ActionAnswer.Fail(503, "test:unread");
            },
            CancellationToken.None);

        var run = await new PluginPipe { Lodestone = lodestone.EndPoint }.ExchangeAsync(pair.Plugin, [1]);

        Assert.Equal("socket:200", await running);
        Assert.Equal(200, run.Status);
        Assert.Equal("Aria Starfall", run.Body.GetProperty("name").GetString());
        Assert.Equal(["open", "close"], run.Texts);
        Assert.Equal(WebSocketState.Closed, pair.Server.State);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task AnUnexpectedMessage_OverDotNetsOwnWebSockets_DropsTheConnection()
    {
        using var server = NewServer();
        using var pair = await SocketPair.OpenAsync();
        using var session = new PipeSession(pair.Server, server.Services.GetRequiredService<LodestoneSockets>(), server.Services.GetRequiredService<PipedReads>(), server.Services.GetRequiredService<IPipedPages>());
        var running = session.RunAsync(
            async (body, cancellation) =>
            {
                await Task.Delay(Timeout.Infinite, cancellation);
                return ActionAnswer.Fail(500, "never");
            },
            CancellationToken.None);

        await pair.Plugin.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        await PluginPipe.SendTextAsync(pair.Plugin, "eof");

        Assert.Equal("socket:cut:unexpected-message", await running.WaitAsync(TimeSpan.FromSeconds(10)));
        await AssertDroppedAsync(pair.Plugin);
    }

    [Fact]
    public async Task OverKestrel_APluginsWebSocket_ChecksThroughThePipe_AndAnOriginIsRefused()
    {
        // The server as deployed, on Kestrel with its WebSocket middleware, rather than the test
        // host's own WebSockets; the plugin's side is .NET's ClientWebSocket.
        using var server = new TestServer();
        server.UseKestrel(0);
        server.Services.GetRequiredService<PipedPages>().TrustForTests = TestAuthority.Trust();
        var origin = new Uri(server.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First());
        var check = new UriBuilder(origin) { Scheme = "ws", Path = CheckPath }.Uri;
        await using var lodestone = new TlsLodestone();
        using var player = new Player(server, new HttpClient { BaseAddress = origin });
        var code = await player.CodeAsync();
        lodestone.Answer = _ => TlsLodestone.Chunked(200, Page("Aria Starfall", "Gilgamesh", code));

        using (var socket = new ClientWebSocket())
        {
            await socket.ConnectAsync(check, CancellationToken.None);
            var run = await new PluginPipe { Lodestone = lodestone.EndPoint }.ExchangeAsync(socket, await player.CheckBodyAsync(Aria, code));
            Assert.Equal(200, run.Status);
            Assert.Equal("Aria Starfall", run.Body.GetProperty("name").GetString());
            Assert.Equal(["open", "close"], run.Texts);
        }

        using (var browser = new ClientWebSocket())
        {
            browser.Options.SetRequestHeader("Origin", "https://plates.example.com");
            browser.Options.CollectHttpResponseDetails = true;
            await Assert.ThrowsAsync<WebSocketException>(() => browser.ConnectAsync(check, CancellationToken.None));
            Assert.Equal(HttpStatusCode.Forbidden, browser.HttpStatusCode);
        }

        await AssertReleasedAsync(server);
        Assert.Equal(1L, await server.CountAsync("SELECT COUNT(*) FROM bindings;"));
    }

    [Fact]
    public async Task AnOriginHeader_IsRefusedBeforeTheUpgrade()
    {
        using var server = NewServer();
        var run = await new PluginPipe { Origin = "https://plates.example.com" }.RunAsync(server, CheckPath, [1]);
        Assert.Equal(403, run.RefusedWith);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task AnAddressGroup_HoldsTwoWebSocketsAtMost_AndTheServerTwenty()
    {
        using var server = NewServer();
        var sockets = server.Services.GetRequiredService<LodestoneSockets>();
        var first = PluginPipe.NextAddress();
        var second = PluginPipe.NextAddress();

        // Two open WebSockets from one address: a third is refused, another address's is not.
        sockets.FirstMessageDeadline = TimeSpan.FromSeconds(10);
        var holding = new[]
        {
            new PluginPipe { Address = first, FirstMessageDelay = TimeSpan.FromSeconds(3) }.RunAsync(server, CheckPath, [1, 2, 3]),
            new PluginPipe { Address = first, FirstMessageDelay = TimeSpan.FromSeconds(3) }.RunAsync(server, CheckPath, [1, 2, 3]),
        };
        await WaitUntilAsync(() => sockets.Open == 2);
        Assert.Equal(503, (await new PluginPipe { Address = first }.RunAsync(server, CheckPath, [1, 2, 3])).RefusedWith);
        Assert.Equal(400, (await new PluginPipe { Address = second }.RunAsync(server, CheckPath, [1, 2, 3])).Status);
        foreach (var run in await Task.WhenAll(holding))
        {
            Assert.Equal(400, run.Status);
        }

        // Each place is released once its session's handler returns, a moment after the plugin sees the close.
        await AssertReleasedAsync(server);

        // An IPv6 address's groups at their multiples: two in its /64, then another /64 of the same /48.
        using (sockets.TryOpen(IPAddress.Parse("2001:db8:0:1::1")))
        using (sockets.TryOpen(IPAddress.Parse("2001:db8:0:1::2")))
        {
            Assert.Null(sockets.TryOpen(IPAddress.Parse("2001:db8:0:1::3")));
            using var otherSubnet = sockets.TryOpen(IPAddress.Parse("2001:db8:0:2::1"));
            Assert.NotNull(otherSubnet);
        }

        // Twenty in all.
        var held = Enumerable.Range(0, LodestoneSockets.Total).Select(_ => sockets.TryOpen(PluginPipe.NextAddress())).ToList();
        Assert.All(held, Assert.NotNull);
        Assert.Null(sockets.TryOpen(PluginPipe.NextAddress()));
        Assert.Equal(503, (await new PluginPipe().RunAsync(server, CheckPath, [1])).RefusedWith);
        held.ForEach(place => place!.Dispose());
        Assert.Equal(400, (await new PluginPipe().RunAsync(server, CheckPath, [1])).Status);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task TheAddressLimit_IsTakenOnce_BeforeTheUpgrade()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone();
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();
        lodestone.Answer = _ => TlsLodestone.Framed(200, Page("Aria Starfall", "Gilgamesh", code));
        var address = PluginPipe.NextAddress();
        var limiter = server.Services.GetRequiredService<RateLimiter>();
        for (var index = 0; index < ServerLimits.LodestonePerAddress.Count - 1; index++)
        {
            Assert.True(limiter.TryTakeAddress(ServerLimits.LodestonePerAddress, address));
        }

        // The last of the address's ten: the signed body doesn't take another, so the check binds.
        var run = await new PluginPipe { Address = address, Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));
        Assert.Equal(200, run.Status);

        // The next WebSocket from that address is refused before its upgrade.
        Assert.Equal(429, (await new PluginPipe { Address = address }.RunAsync(server, CheckPath, [1])).RefusedWith);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task APipedCheckAndReread_StillCountAgainstTheIdsAndTheKeysLimits()
    {
        using var server = NewServer();
        var limiter = server.Services.GetRequiredService<RateLimiter>();

        // C6's ten checks a day for one Lodestone id from one address range.
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();
        var address = PluginPipe.NextAddress();
        for (var index = 0; index < ServerLimits.ChecksPerLodestoneId.Count; index++)
        {
            Assert.True(limiter.TryTake(ServerLimits.ChecksPerLodestoneId, Aria + "|4/" + address));
        }

        var check = await new PluginPipe { Address = address }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));
        Assert.Equal(429, check.Status);
        Assert.Empty(check.Texts);

        // C6's ten an hour for one key.
        using var bound = server.NewPlayer();
        await bound.BindAsync(Bram, "Bram Oakes", "Gilgamesh");
        while (limiter.TryTake(ServerLimits.LodestonePerKey, bound.Key.PublicKey.Id.ToString()))
        {
        }

        var reread = await new PluginPipe().RunAsync(server, RereadPath, await bound.RereadBodyAsync());
        Assert.Equal(429, reread.Status);
        Assert.Empty(reread.Texts);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task APipedRead_TakesNeitherTheRelaysLock_NorTheHoursBudget()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone();
        using var stalled = server.NewPlayer();
        using var player = server.NewPlayer();
        var budget = server.Services.GetRequiredService<LodestoneBudget>();

        // A POST check whose read through the server's own client never ends holds the relay's lock.
        server.Lodestone.Pages[Bram] = new LodestoneResponse(FakeLodestone.Stall, null);
        var stalledCode = await stalled.CodeAsync();
        var stalledBody = await stalled.SignAsync(RequestProofKind.LodestoneCheck, $"{{\"lodestoneId\":\"{Bram}\",\"code\":\"{stalledCode}\",\"name\":\"Bram Oakes\",\"world\":\"Gilgamesh\"}}");
        using var stop = new CancellationTokenSource();
        using var content = new ByteArrayContent(stalledBody);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var stalling = stalled.Client.PostAsync(CheckPath, content, stop.Token);
        await WaitUntilAsync(() => server.Lodestone.Fetched.Contains(Bram));

        // And the hour's budget is spent.
        while (budget.TryAcquire(reread: false))
        {
            budget.Release();
        }

        Assert.False(budget.HasRoom(reread: false));

        var code = await player.CodeAsync();
        lodestone.Answer = _ => TlsLodestone.Framed(200, Page("Aria Starfall", "Gilgamesh", code));
        var run = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));
        Assert.Equal(200, run.Status);
        Assert.False(stalling.IsCompleted);
        Assert.False(budget.HasRoom(reread: false));

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stalling);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task ThePipesPlaces_AreTwenty_TakenOnlyAfterTheCode()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone();
        using var player = server.NewPlayer();
        var reads = server.Services.GetRequiredService<PipedReads>();
        var code = await player.CodeAsync();
        lodestone.Answer = _ => TlsLodestone.Framed(200, Page("Aria Starfall", "Gilgamesh", code));

        var taken = Enumerable.Range(0, PipedReads.Max).Select(_ => reads.TryTake()).ToList();
        Assert.All(taken, Assert.NotNull);
        Assert.Null(reads.TryTake());

        // With every place taken, a check with a wrong code fails as ever, and one with the right
        // code is "try again later": either way the pipe never opens.
        var wrong = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, "AF-0000000000"));
        Assert.Equal(422, wrong.Status);
        Assert.Empty(wrong.Texts);
        var busy = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));
        Assert.Equal(503, busy.Status);
        Assert.Null(busy.Reason);
        Assert.Empty(busy.Texts);
        Assert.Equal(0, lodestone.Connections);

        taken.ForEach(place => place!.Dispose());
        var bound = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));
        Assert.Equal(200, bound.Status);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task APipedCheck_OpensThePipeBeforeTheAllowlist_AndEveryLaterFailureLeavesAlike()
    {
        var afterRead = TimeSpan.FromSeconds(3);
        using var server = NewServer(afterRead);
        await using var lodestone = new TlsLodestone();
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();

        // An id off the allowlist: the pipe opens, and its page is read, before the allowlist says no.
        lodestone.Answer = _ => TlsLodestone.Framed(200, Page("Aria Starfall", "Gilgamesh", code));
        var offList = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(OffTheAllowlist, code));
        Assert.Equal(["open", "close"], offList.Texts);
        Assert.StartsWith("GET /lodestone/character/45678901/ HTTP/1.1\r\n", Assert.Single(lodestone.Requests), StringComparison.Ordinal);

        // An id on it, whose page lacks the code: a later failure.
        lodestone.Answer = _ => TlsLodestone.Framed(200, Page("Aria Starfall", "Gilgamesh", "no code here"));
        var noCode = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));
        Assert.Equal(["open", "close"], noCode.Texts);

        // The same answer, no sooner than the fixed delay after the read ended, for both. "close" is
        // sent as the read ends; the slack covers this side seeing it late on a loaded runner.
        foreach (var run in new[] { offList, noCode })
        {
            Assert.Equal("{\"status\":422}", run.Final!.Value.GetRawText());
            Assert.True(run.FinalAt - run.CloseAt >= afterRead - TimeSpan.FromSeconds(1), $"the answer came {run.FinalAt - run.CloseAt} after close");
        }

        await AssertLoggedAsync(server, "socket:422:check:allowlist");
        await AssertLoggedAsync(server, "socket:422:check:page");

        // A wrong code fails before the pipe would open.
        var wrongCode = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, "AF-0000000000"));
        Assert.Equal(422, wrongCode.Status);
        Assert.Empty(wrongCode.Texts);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task APipedReread_WithNoBinding_OrATakenOverOne_OpensNoPipe()
    {
        using var server = NewServer();
        using var stranger = server.NewPlayer();
        var unbound = await new PluginPipe().RunAsync(server, RereadPath, await stranger.RereadBodyAsync());
        Assert.Equal(404, unbound.Status);
        Assert.Empty(unbound.Texts);

        using var oldPc = server.NewPlayer();
        using var newPc = server.NewPlayer();
        await oldPc.BindAsync(Aria);
        await newPc.BindAsync(Aria);
        var takenOver = await new PluginPipe().RunAsync(server, RereadPath, await oldPc.RereadBodyAsync());
        Assert.Equal(410, takenOver.Status);
        Assert.Empty(takenOver.Texts);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task ARefusedChallenge_IsAFinal409_CarryingAFreshChallenge()
    {
        using var server = NewServer();
        using var player = server.NewPlayer();
        var body = await player.RereadBodyAsync();
        Assert.Equal(404, (await new PluginPipe().RunAsync(server, RereadPath, body)).Status);

        // The same body again: its challenge is used up.
        var again = await new PluginPipe().RunAsync(server, RereadPath, body);
        Assert.Equal(409, again.Status);
        var fresh = RequestChallenge.FromBytes(Convert.FromBase64String(again.Final!.Value.GetProperty("challenge").GetString()!));
        using var retried = await player.SendAsync(RereadPath, RequestProofKind.LodestoneReread, "{}", challenge: fresh);
        Assert.Equal(HttpStatusCode.NotFound, retried.StatusCode);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task ABindingNotReadFor30Days_StopsAnsweringLookups_ButItsKeyStillFindsIt()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone { Answer = _ => TlsLodestone.Framed(200, Page("Aria Starfall", "Gilgamesh", "")) };
        using var aria = server.NewPlayer();
        using var bram = server.NewPlayer();
        var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
        await bram.BindAsync(Bram, "Bram Oakes", "Gilgamesh");
        using (var published = await aria.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Plate"), aria.Key)))
        {
            Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
        }

        async Task<HttpStatusCode> LookUpAriaAsync()
        {
            using var lookup = await bram.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}");
            return lookup.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await LookUpAriaAsync());

        // Through the 29th day after its read it answers; from the 30th it doesn't.
        server.Time.Advance(TimeSpan.FromDays(29));
        Assert.Equal(HttpStatusCode.OK, await LookUpAriaAsync());
        server.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(HttpStatusCode.NotFound, await LookUpAriaAsync());
        Assert.Null(await server.Services.GetRequiredService<BindingStore>().FindShownAsync("aria starfall", "Gilgamesh", default));

        // Its key still finds it: it publishes, and its own re-read brings it back.
        using (var publishedAgain = await aria.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Plate again"), aria.Key)))
        {
            Assert.Equal(HttpStatusCode.NoContent, publishedAgain.StatusCode);
        }

        var reread = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, RereadPath, await aria.RereadBodyAsync());
        Assert.Equal(200, reread.Status);
        Assert.Equal(HttpStatusCode.OK, await LookUpAriaAsync());

        // A stale binding still opts out.
        server.Time.Advance(TimeSpan.FromDays(40));
        Assert.Equal(HttpStatusCode.NotFound, await LookUpAriaAsync());
        using (var optOut = await aria.SendAsync("/v1/opt-out", RequestProofKind.OptOut, "{}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, optOut.StatusCode);
        }

        Assert.Equal(0L, await server.CountAsync($"SELECT COUNT(*) FROM bindings WHERE lodestone_id = {Aria};"));
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task ACheck_AndAPostReread_RecordTheDayOfTheRead()
    {
        using var server = NewServer();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria);
        Assert.Equal(Today(server), await server.CountAsync("SELECT read_day FROM bindings;"));

        server.Time.Advance(TimeSpan.FromDays(31));
        Assert.Null(await server.Services.GetRequiredService<BindingStore>().FindShownAsync("aria starfall", "Gilgamesh", default));
        using (var reread = await player.SendAsync(RereadPath, RequestProofKind.LodestoneReread, "{}"))
        {
            Assert.Equal(HttpStatusCode.OK, reread.StatusCode);
        }

        Assert.Equal(Today(server), await server.CountAsync("SELECT read_day FROM bindings;"));
        Assert.NotNull(await server.Services.GetRequiredService<BindingStore>().FindShownAsync("aria starfall", "Gilgamesh", default));

        // A "not found" is no read: the day stays.
        server.Time.Advance(TimeSpan.FromDays(3));
        server.Lodestone.Pages.Clear();
        using (var notFound = await player.SendAsync(RereadPath, RequestProofKind.LodestoneReread, "{}"))
        {
            Assert.Equal(HttpStatusCode.OK, notFound.StatusCode);
        }

        Assert.Equal(Today(server) - 3, await server.CountAsync("SELECT read_day FROM bindings;"));
    }

    [Fact]
    public async Task ADatabaseFromBeforeTheReadDay_GivesItsBindingsTheDayOfTheChange()
    {
        using var server = new TestServer();
        using var key = EcdsaPersonaSigner.CreateEphemeral();
        await using (var old = new SqliteConnection("Data Source=" + server.DatabasePath + ";Pooling=False"))
        {
            await old.OpenAsync();
            await using var command = old.CreateCommand();
            command.CommandText = """
                CREATE TABLE bindings (
                    persona TEXT PRIMARY KEY,
                    lodestone_id INTEGER NOT NULL UNIQUE,
                    name TEXT NOT NULL,
                    name_key TEXT NOT NULL,
                    world TEXT NOT NULL,
                    profile_id TEXT NOT NULL UNIQUE,
                    hidden INTEGER NOT NULL DEFAULT 0,
                    not_found_day INTEGER
                ) WITHOUT ROWID;
                INSERT INTO bindings VALUES ($persona, 12345678, 'Aria Starfall', 'aria starfall', 'Gilgamesh', $profile, 0, NULL);
                """;
            command.Parameters.AddWithValue("$persona", key.PublicKey.Id.ToString());
            command.Parameters.AddWithValue("$profile", ProfileId.NewId().ToString());
            await command.ExecuteNonQueryAsync();
        }

        // The server starts on it: the binding gets the day of the change, and answers lookups.
        var store = server.Services.GetRequiredService<BindingStore>();
        var day = Today(server);
        Assert.Equal(day, await server.CountAsync("SELECT read_day FROM bindings;"));
        Assert.NotNull(await store.FindShownAsync("aria starfall", "Gilgamesh", default));
        Assert.NotNull(await store.FindByPersonaAsync(key.PublicKey.Id, default));

        // Starting again changes nothing.
        server.Time.Advance(TimeSpan.FromDays(3));
        await server.Services.GetRequiredService<ServerDatabase>().InitializeAsync(default);
        Assert.Equal(day, await server.CountAsync("SELECT read_day FROM bindings;"));

        // Thirty days after the change, it stops answering lookups until it is read.
        server.Time.Advance(TimeSpan.FromDays(27));
        Assert.Null(await store.FindShownAsync("aria starfall", "Gilgamesh", default));
        Assert.NotNull(await store.FindByPersonaAsync(key.PublicKey.Id, default));
    }

    [Fact]
    public async Task TheDailyReread_RunsOnlyWhileARelayIsSet_AndRecordsTheRead()
    {
        using var server = NewServer();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria);
        var fetched = server.Lodestone.Fetched.Count;

        // Without a relay the schedule ends at once (since .NET 10 a background service starts on the
        // thread pool, so its task is awaited); with one, it waits for its first turn.
        using (var withoutRelay = Schedule(server, ""))
        {
            await withoutRelay.StartAsync(default);
            await withoutRelay.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(withoutRelay.ExecuteTask.IsCompletedSuccessfully);
            await withoutRelay.StopAsync(default);
        }

        using (var withRelay = Schedule(server, "100.101.102.103:8443"))
        {
            await withRelay.StartAsync(default);
            await Task.Delay(500);
            Assert.False(withRelay.ExecuteTask!.IsCompleted);
            await withRelay.StopAsync(default);
        }

        Assert.Equal(fetched, server.Lodestone.Fetched.Count);

        // Its re-read, like a player's, records the read: a binding 30 days unread answers again.
        server.Time.Advance(TimeSpan.FromDays(30));
        var store = server.Services.GetRequiredService<BindingStore>();
        Assert.Null(await store.FindShownAsync("aria starfall", "Gilgamesh", default));
        await server.Services.GetRequiredService<Rereads>().RereadAsync(player.Key.PublicKey.Id, default);
        Assert.NotNull(await store.FindShownAsync("aria starfall", "Gilgamesh", default));
    }

    [Fact]
    public async Task APipedSessionsLog_HoldsNoIdentifierCodeNameOrResponseHeader()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone();
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();
        lodestone.Answer = _ => TlsLodestone.Framed(200, Page("Aria Starfall", "Gilgamesh", code));
        var bound = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));
        Assert.Equal(200, bound.Status);
        lodestone.Answer = _ => TlsLodestone.Framed(403, "<html>Request blocked</html>");
        Assert.Equal(503, (await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, RereadPath, await player.RereadBodyAsync())).Status);
        await using (var forged = new TlsLodestone(TestAuthority.OwnCertificate))
        {
            Assert.Equal(503, (await new PluginPipe { Lodestone = forged.EndPoint }.RunAsync(server, RereadPath, await player.RereadBodyAsync())).Status);
        }

        await AssertReleasedAsync(server);
        await AssertLoggedAsync(server, "socket:200");
        await AssertLoggedAsync(server, "socket:503:reread:Refused");
        await AssertLoggedAsync(server, "socket:503:reread:Unanswered");
        var log = server.Log.All;
        Assert.Contains("/v1/lodestone/check", log, StringComparison.Ordinal);
        foreach (var secret in new[]
        {
            player.Key.PublicKey.Id.ToString(),
            player.Key.PublicKey.Id.ToString()[4..],
            Aria.ToString(System.Globalization.CultureInfo.InvariantCulture),
            code,
            code[3..],
            bound.Body.GetProperty("profileId").GetString()!,
            "Aria",
            "Starfall",
            "Gilgamesh",
            "na.finalfantasyxiv.com",
            "X-Amz-Cf-Pop",
            TlsLodestone.EdgeHeaderValue,
            "Request blocked",
        })
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ALookup_HidesABindingNotReadFor30Days_OnlyWhileNoRelayIsSet()
    {
        // While an operator relay is set, the daily re-read keeps the day of the last read, but only
        // while the relay is open, so lookups don't hide by it; without a relay, they do.
        foreach (var (relay, expected) in new[] { ("100.101.102.103:8443", HttpStatusCode.OK), ("", HttpStatusCode.NotFound) })
        {
            using var server = new TestServer { LodestoneRelay = relay };
            using var aria = server.NewPlayer();
            using var bram = server.NewPlayer();
            var profile = ProfileId.Parse((await aria.BindAsync(Aria)).GetProperty("profileId").GetString()!);
            await bram.BindAsync(Bram, "Bram Oakes", "Gilgamesh");
            using (var published = await aria.PublishDocumentAsync(SignedDocumentCodec.Sign(Plates.Snapshot(profile, "Plate"), aria.Key)))
            {
                Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
            }

            server.Time.Advance(TimeSpan.FromDays(45));
            using var lookup = await bram.SendAsync("/v1/lookup", RequestProofKind.Lookup, "{\"name\":\"Aria Starfall\",\"world\":\"Gilgamesh\"}");
            Assert.Equal(expected, lookup.StatusCode);
        }
    }

    [Fact]
    public async Task TheSameKeyCheckingAgain_RecordsTheDayOfTheRead()
    {
        using var server = NewServer();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria);
        var first = Today(server);
        server.Time.Advance(TimeSpan.FromDays(10));

        await player.BindAsync(Aria);

        Assert.Equal(first + 10, await server.CountAsync("SELECT read_day FROM bindings;"));
    }

    [Fact]
    public async Task ReadsThatFail_OrFindTheNotFoundPageOnce_LeaveTheDayOfTheLastRead()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria);
        var day = Today(server);
        server.Time.Advance(TimeSpan.FromDays(5));

        // The plugin couldn't connect.
        var failed = await new PluginPipe { OnOpen = OpenAnswer.Fail }.RunAsync(server, RereadPath, await player.RereadBodyAsync());
        Assert.Equal(503, failed.Status);

        // The Lodestone's own "not found" page, the first time: the binding stays, and wasn't read.
        lodestone.Answer = _ => TlsLodestone.Framed(404, LodestoneHtml.NotFoundPage);
        var notFound = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, RereadPath, await player.RereadBodyAsync());
        Assert.Equal(200, notFound.Status);

        // A POST re-read whose fetch fails.
        server.Lodestone.Pages[Aria] = new LodestoneResponse(0, null);
        using (var refused = await player.SendAsync(RereadPath, RequestProofKind.LodestoneReread, "{}"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        }

        Assert.Equal(day, await server.CountAsync("SELECT read_day FROM bindings;"));
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task APipedReread_OfABindingOffTheAllowlist_Is404_AndOpensNoPipe()
    {
        using var server = NewServer();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria);
        await ExecuteAsync(server.DatabasePath, "UPDATE bindings SET lodestone_id = " + OffTheAllowlist + ";");

        var run = await new PluginPipe().RunAsync(server, RereadPath, await player.RereadBodyAsync());

        Assert.Equal(404, run.Status);
        Assert.Empty(run.Texts);
        await AssertReleasedAsync(server);
    }

    [Fact]
    public async Task AStaleBinding_IsStillDisplacedByANewerRead_AndTakenOverByANewKey()
    {
        using var server = NewServer();
        using var aria = server.NewPlayer();
        using var newcomer = server.NewPlayer();
        using var newKey = server.NewPlayer();
        await aria.BindAsync(Aria);
        server.Time.Advance(TimeSpan.FromDays(40));
        var store = server.Services.GetRequiredService<BindingStore>();

        // Another character now shows the same name and World: its check displaces the stale binding.
        await newcomer.BindAsync(Bram, "Aria Starfall", "Gilgamesh");
        Assert.Equal(Bram, (await store.FindShownAsync("aria starfall", "Gilgamesh", default))?.LodestoneId);

        // A new key checks the stale character: the binding moves to it, read today, and the old key learns so.
        await newKey.BindAsync(Aria);
        Assert.Equal(Today(server), await server.CountAsync("SELECT read_day FROM bindings WHERE lodestone_id = " + Aria + ";"));
        using var reread = await aria.SendAsync(RereadPath, RequestProofKind.LodestoneReread, "{}");
        Assert.Equal(HttpStatusCode.Gone, reread.StatusCode);
    }

    [Fact]
    public async Task APipedReread_OvertakenByATakeover_AnswersTakenOver()
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone();
        using var player = server.NewPlayer();
        using var newKey = server.NewPlayer();
        await player.BindAsync(Aria);
        var store = server.Services.GetRequiredService<BindingStore>();

        // While the page is on its way, another key takes the character over.
        lodestone.Answer = _ =>
        {
            store.BindCharacterAsync(newKey.Key.PublicKey.Id, Aria, new LodestoneCharacter("Aria Starfall", "Gilgamesh", ""), default).GetAwaiter().GetResult();
            return TlsLodestone.Framed(200, Page("Aria Starfall", "Gilgamesh", ""));
        };
        var run = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, RereadPath, await player.RereadBodyAsync());

        Assert.Equal(410, run.Status);
        await AssertReleasedAsync(server);
        await AssertLoggedAsync(server, "socket:410:reread:taken-over");
    }

    [Fact]
    public async Task FreshAndMigratedDatabases_DefineTheDayOfTheLastReadAlike()
    {
        using var fresh = new TestServer();
        _ = fresh.Services.GetRequiredService<BindingStore>();
        using var migrated = new TestServer();
        await CreateBindingsBeforeTheReadDayAsync(migrated.DatabasePath);
        _ = migrated.Services.GetRequiredService<BindingStore>();

        Assert.Equal("INTEGER|1|0", await ReadDayColumnAsync(fresh.DatabasePath));
        Assert.Equal("INTEGER|1|0", await ReadDayColumnAsync(migrated.DatabasePath));
    }

    [Fact]
    public async Task ABindingAServerFromBeforeTheReadDayWrote_GetsTheDayOfTheNextStart()
    {
        using var server = NewServer();
        using var key = EcdsaPersonaSigner.CreateEphemeral();
        var store = server.Services.GetRequiredService<BindingStore>();

        // As an older server writes it after a rollback: without the column, so it takes its default.
        await ExecuteAsync(
            server.DatabasePath,
            "INSERT INTO bindings (persona, lodestone_id, name, name_key, world, profile_id, hidden, not_found_day) VALUES ('" + key.PublicKey.Id + "', " + Aria + ", 'Aria Starfall', 'aria starfall', 'Gilgamesh', '" + ProfileId.NewId() + "', 0, NULL);");
        Assert.Equal(0L, await server.CountAsync("SELECT read_day FROM bindings;"));
        Assert.Null(await store.FindShownAsync("aria starfall", "Gilgamesh", default));

        server.Time.Advance(TimeSpan.FromDays(2));
        await server.Services.GetRequiredService<ServerDatabase>().InitializeAsync(default);

        Assert.Equal(Today(server), await server.CountAsync("SELECT read_day FROM bindings;"));
        Assert.NotNull(await store.FindShownAsync("aria starfall", "Gilgamesh", default));
    }

    [Fact]
    public async Task TwoServersStartingTogether_OnAFileFromBefore_AddTheDayOfTheLastReadOnce()
    {
        var folder = Directory.CreateTempSubdirectory("af-read-day-");
        var path = Path.Combine(folder.FullName, "server.db");
        try
        {
            await CreateBindingsBeforeTheReadDayAsync(path);
            var options = Options.Create(new ServerOptions { DatabasePath = path });
            var first = new ServerDatabase(options, NullLogger<ServerDatabase>.Instance);
            var second = new ServerDatabase(options, NullLogger<ServerDatabase>.Instance);

            await Task.WhenAll(first.InitializeAsync(default), second.InitializeAsync(default));

            Assert.Equal("INTEGER|1|0", await ReadDayColumnAsync(path));
        }
        finally
        {
            // Only this file's pool: clearing every pool would close other tests' connections.
            SqliteConnection.ClearPool(new SqliteConnection(ServerDatabase.ConnectionStringFor(path)));
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AdminCharacters_ShowsTheDayOfEachBindingsLastRead()
    {
        using var server = NewServer();
        using var player = server.NewPlayer();
        await player.BindAsync(Aria);
        using var output = new StringWriter();

        await AdminCommands.RunAsync(["characters"], server.Services.GetRequiredService<ServerDatabase>(), server.Services.GetRequiredService<BindingStore>(), output);

        Assert.Contains(server.Time.Now.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A database file as a server from before the day of the last read left it: the bindings table without the column.</summary>
    private static Task CreateBindingsBeforeTheReadDayAsync(string path) => ExecuteAsync(path, """
        CREATE TABLE bindings (
            persona TEXT PRIMARY KEY,
            lodestone_id INTEGER NOT NULL UNIQUE,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL,
            world TEXT NOT NULL,
            profile_id TEXT NOT NULL UNIQUE,
            hidden INTEGER NOT NULL DEFAULT 0,
            not_found_day INTEGER
        ) WITHOUT ROWID;
        """);

    /// <summary>The day of the last read's column as SQLite records it: its type, whether it is NOT NULL, and its default.</summary>
    private static async Task<string> ReadDayColumnAsync(string path)
    {
        await using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type || '|' || \"notnull\" || '|' || dflt_value FROM pragma_table_info('bindings') WHERE name = 'read_day';";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static TestServer NewServer(TimeSpan? afterRead = null)
    {
        var server = new TestServer { CheckFailureAfterRead = afterRead ?? TimeSpan.Zero };
        server.Services.GetRequiredService<PipedPages>().TrustForTests = TestAuthority.Trust();
        return server;
    }

    private static string Page(string name, string world, string introduction, string dataCenter = "Aether") =>
        LodestoneHtml.Character(name, world, introduction, dataCenter).Html!;

    private static long Today(TestServer server) => server.Time.Now.ToUnixTimeSeconds() / 86_400;

    /// <summary>A check and a re-read through a stand-in answering with <paramref name="certificate"/>: no request reaches it, and both are "try again later".</summary>
    private static async Task AssertNoRequestReachesAsync(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, SslProtocols protocols = SslProtocols.Tls12 | SslProtocols.Tls13)
    {
        using var server = NewServer();
        await using var lodestone = new TlsLodestone(certificate, protocols);
        using var player = server.NewPlayer();
        var code = await player.CodeAsync();
        lodestone.Answer = _ => TlsLodestone.Framed(200, Page("Aria Starfall", "Gilgamesh", code));

        var check = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, CheckPath, await player.CheckBodyAsync(Aria, code));
        Assert.Equal(503, check.Status);
        Assert.Null(check.Reason);
        Assert.Equal(["open", "close"], check.Texts);
        Assert.Equal(1, lodestone.Connections);
        Assert.False(lodestone.SawRequest);
        Assert.Equal(0L, await server.CountAsync("SELECT COUNT(*) FROM bindings;"));

        await player.BindAsync(Aria);
        var reread = await new PluginPipe { Lodestone = lodestone.EndPoint }.RunAsync(server, RereadPath, await player.RereadBodyAsync());
        Assert.Equal(503, reread.Status);
        Assert.False(lodestone.SawRequest);
        Assert.Equal(0L, await server.CountAsync("SELECT COUNT(*) FROM bindings WHERE not_found_day IS NOT NULL;"));
        await AssertReleasedAsync(server);
    }

    /// <summary>Waits until every WebSocket place and piped read's place is free again: they are released on every exit.</summary>
    private static async Task AssertReleasedAsync(TestServer server)
    {
        var sockets = server.Services.GetRequiredService<LodestoneSockets>();
        var reads = server.Services.GetRequiredService<PipedReads>();
        await WaitUntilAsync(() => sockets.Open == 0 && reads.InUse == 0, throwOnTimeout: false);
        Assert.Equal(0, sockets.Open);
        Assert.Equal(0, reads.InUse);
    }

    /// <summary>
    /// Waits for the request log to hold <paramref name="text"/>: a request's line is written once its
    /// handler returns, a moment after the plugin's side has seen the WebSocket close.
    /// </summary>
    private static async Task AssertLoggedAsync(TestServer server, string text)
    {
        await WaitUntilAsync(() => server.Log.All.Contains(text, StringComparison.Ordinal), throwOnTimeout: false);
        Assert.Contains(text, server.Log.All, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, bool throwOnTimeout = true)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                Assert.False(throwOnTimeout, "The condition never held.");
                return;
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Waits for <paramref name="socket"/>'s peer to drop it, or to close it with no further message.</summary>
    private static async Task AssertDroppedAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[1024];
        try
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            // Dropped.
        }
    }

    /// <summary>The plugin's side over a raw socket: the first message, <c>opened</c> at <c>open</c>, then every binary message counted until the final one.</summary>
    private static async Task<PipeRun> ExchangeCountingAsync(WebSocket socket, List<int> received)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = new PipeRun();
        await socket.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, endOfMessage: true, timeout.Token);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
                return run;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                received.Add((int)message.Length);
                continue;
            }

            var text = Encoding.UTF8.GetString(message.ToArray());
            if (text == "open")
            {
                run.Texts.Add(text);
                await PluginPipe.SendTextAsync(socket, "opened");
            }
            else if (text == "close")
            {
                run.Texts.Add(text);
            }
            else
            {
                using var final = System.Text.Json.JsonDocument.Parse(text);
                run.Final = final.RootElement.Clone();
            }
        }
    }

    private static Rereads Schedule(TestServer server, string relay)
    {
        var options = new ServerOptions { DeploymentName = TestServer.Deployment, AllowTestDeploymentName = true, DatabasePath = server.DatabasePath, LodestoneRelay = relay };
        options.Validate();
        return new Rereads(
            server.Services.GetRequiredService<BindingStore>(),
            server.Services.GetRequiredService<LodestoneReader>(),
            server.Services.GetRequiredService<Allowlist>(),
            Options.Create(options),
            server.Time,
            NullLogger<Rereads>.Instance);
    }

    /// <summary>A fetch that writes <paramref name="allowed"/> bytes toward the Lodestone, then one more, and records the refusal.</summary>
    private sealed class WritingPages(int allowed) : IPipedPages
    {
        public bool Refused { get; private set; }

        public async Task<LodestoneRead> ReadAsync(long lodestoneId, Stream pipe, CancellationToken deadline, CancellationToken session)
        {
            await pipe.WriteAsync(new byte[allowed], deadline);
            try
            {
                await pipe.WriteAsync(new byte[1], deadline);
            }
            catch (IOException)
            {
                Refused = true;
            }

            return new LodestoneRead(LodestoneOutcome.Unanswered);
        }
    }

    /// <summary>Two ends of one WebSocket over a loopback TCP connection, .NET's own implementation on both, as Kestrel and the plugin use it.</summary>
    private sealed class SocketPair : IDisposable
    {
        private readonly TcpClient near;
        private readonly TcpClient far;

        private SocketPair(TcpClient near, TcpClient far)
        {
            this.near = near;
            this.far = far;
            Server = WebSocket.CreateFromStream(far.GetStream(), new WebSocketCreationOptions { IsServer = true });
            Plugin = WebSocket.CreateFromStream(near.GetStream(), new WebSocketCreationOptions { IsServer = false });
        }

        public WebSocket Server { get; }

        public WebSocket Plugin { get; }

        public static async Task<SocketPair> OpenAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var near = new TcpClient();
                var accepting = listener.AcceptTcpClientAsync();
                await near.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
                return new SocketPair(near, await accepting);
            }
            finally
            {
                listener.Stop();
            }
        }

        public void Dispose()
        {
            Server.Dispose();
            Plugin.Dispose();
            near.Dispose();
            far.Dispose();
        }
    }
}
