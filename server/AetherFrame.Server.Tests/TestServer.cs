using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Requests;
using AetherFrame.Protocol.Signing;
using AetherFrame.Server.Lodestone;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AetherFrame.Server.Tests;

/// <summary>
/// One server, in memory, with its own database in a temporary folder, a manual clock, a fake
/// Lodestone and a log that the tests read. Disposing it deletes the folder.
/// </summary>
internal sealed class TestServer : WebApplicationFactory<Program>
{
    public const string Deployment = "plates.example.com";

    /// <summary>The Lodestone ids the allowlist holds.</summary>
    public static readonly long[] Allowed = [12345678, 23456789, 34567890];

    private readonly string folder = Path.Combine(Path.GetTempPath(), "aetherframe-server-tests", Guid.NewGuid().ToString("N"));

    public TestServer()
    {
        Directory.CreateDirectory(folder);
    }

    public ManualTime Time { get; } = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));

    public FakeLodestone Lodestone { get; } = new();

    public CapturedLog Log { get; } = new();

    /// <summary>Whether the server talks to real worker runs over <see cref="ImageWorkerSocket"/>, rather than to <see cref="Images"/>. Set before the server starts.</summary>
    public bool UseImageWorker { get; set; }

    /// <summary>
    /// The worker socket, in a short folder of its own: a Unix socket's path is limited to about 108
    /// bytes, and some temporary folders are long.
    /// </summary>
    public string ImageWorkerSocket { get; } = Path.Combine(Path.GetTempPath(), "afw-" + Guid.NewGuid().ToString("N")[..12], "i.sock");

    /// <summary>The image worker: by default, it returns what it's given.</summary>
    public FakeImages Images { get; } = new();

    /// <summary>Runs a query that answers one number, on the server's database.</summary>
    public async Task<long> CountAsync(string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The failed check's floor: none, unless a test sets one before the server starts.</summary>
    public TimeSpan CheckFailureFloor { get; set; } = TimeSpan.Zero;

    public string DatabasePath => Path.Combine(folder, "server.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // As deployed: no developer exception page, which would log and answer with an exception's details.
        builder.UseEnvironment("Production");
        builder.UseSetting("AetherFrame:DeploymentName", Deployment);
        builder.UseSetting("AetherFrame:AllowTestDeploymentName", "true");
        builder.UseSetting("AetherFrame:DatabasePath", DatabasePath);
        builder.UseSetting("AetherFrame:RereadsEnabled", "false");
        if (UseImageWorker)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ImageWorkerSocket)!);
            builder.UseSetting("AetherFrame:ImageWorkerSocket", ImageWorkerSocket);
        }
        for (var index = 0; index < Allowed.Length; index++)
        {
            builder.UseSetting($"AetherFrame:AllowedLodestoneIds:{index}", Allowed[index].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.ConfigureLogging(logging => logging.AddProvider(Log));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);

            // The real Lodestone client, logging and all, with only its connection replaced.
            services.AddHttpClient(LodestoneHttpPages.ClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeLodestoneHandler(Lodestone));
            services.PostConfigure<ServerOptions>(options => options.CheckFailureFloor = CheckFailureFloor);
            if (!UseImageWorker)
            {
                services.RemoveAll<AetherFrame.Server.Images.IImageProcessor>();
                services.AddSingleton<AetherFrame.Server.Images.IImageProcessor>(Images);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { folder, Path.GetDirectoryName(ImageWorkerSocket)! })
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A player's plugin: one key, talking to this server.</summary>
    public Player NewPlayer() => new(this, CreateClient());
}

/// <summary>A player: one ephemeral key and a client, signing requests as the plugin will (ServerApi-v1.md, section 2).</summary>
internal sealed class Player(TestServer server, HttpClient client) : IDisposable
{
    public EcdsaPersonaSigner Key { get; } = EcdsaPersonaSigner.CreateEphemeral();

    public HttpClient Client => client;

    public TestServer Server => server;

    public async Task<RequestChallenge> ChallengeAsync()
    {
        using var response = await client.PostAsync("/v1/challenge", new ByteArrayContent([]));
        response.EnsureSuccessStatusCode();
        return ToChallenge(await response.Content.ReadAsByteArrayAsync());
    }

    private static RequestChallenge ToChallenge(byte[] bytes) => RequestChallenge.FromBytes(bytes);

    /// <summary>Signs <paramref name="body"/> as <paramref name="kind"/> under a fresh challenge and posts it to <paramref name="path"/>.</summary>
    public async Task<HttpResponseMessage> SendAsync(string path, RequestProofKind kind, string body, string deployment = TestServer.Deployment, RequestChallenge? challenge = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var signedUnder = challenge ?? await ChallengeAsync();
        var proof = RequestProofCodec.SignAction(kind, bytes, DeploymentName.Parse(deployment), signedUnder, Key);
        return await PostRawAsync(path, Envelope(proof, bytes));
    }

    public Task<HttpResponseMessage> PostRawAsync(string path, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return client.PostAsync(path, content);
    }

    public static byte[] Envelope(byte[] proof, byte[] payload)
    {
        var body = new byte[2 + proof.Length + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, (ushort)proof.Length);
        proof.CopyTo(body, 2);
        payload.CopyTo(body, 2 + proof.Length);
        return body;
    }

    /// <summary>Asks for a code, puts it on the character's fake Lodestone page, and checks it.</summary>
    public async Task<JsonElement> BindAsync(long lodestoneId, string name = "Aria Starfall", string world = "Gilgamesh")
    {
        var code = await CodeAsync();
        server.Lodestone.Pages[lodestoneId] = LodestoneHtml.Character(name, world, "Hello! " + code + " Thanks.");
        using var response = await CheckAsync(lodestoneId, code);
        Xunit.Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonElementAsync();
    }

    public async Task<string> CodeAsync()
    {
        using var response = await SendAsync("/v1/lodestone/code", RequestProofKind.LodestoneCode, "{}");
        Xunit.Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonElementAsync()).GetProperty("code").GetString()!;
    }

    /// <summary>Signs <paramref name="snapshot"/> and publishes it with <paramref name="images"/>.</summary>
    public Task<HttpResponseMessage> PublishAsync(AetherFrame.Protocol.Remote.ProfileLayoutSnapshot snapshot, params byte[][] images) =>
        PublishDocumentAsync(AetherFrame.Protocol.Documents.SignedDocumentCodec.Sign(snapshot, Key), images);

    /// <summary>Publishes a signed document with a fresh proof of it (ServerApi-v1.md, section 2.2).</summary>
    public async Task<HttpResponseMessage> PublishDocumentAsync(byte[] document, params byte[][] images)
    {
        var challenge = await ChallengeAsync();
        var proof = RequestProofCodec.Sign(document, DeploymentName.Parse(TestServer.Deployment), challenge, Key);
        return await PostRawAsync("/v1/publish", Envelope(proof, Plates.PublishPayload(document, images)));
    }

    public Task<HttpResponseMessage> CheckAsync(long lodestoneId, string code) =>
        SendAsync("/v1/lodestone/check", RequestProofKind.LodestoneCheck, $"{{\"lodestoneId\":\"{lodestoneId}\",\"code\":\"{code}\"}}");

    public void Dispose()
    {
        Key.Dispose();
        client.Dispose();
    }
}

internal static class HttpContentJson
{
    public static async Task<JsonElement> ReadFromJsonElementAsync(this HttpContent content)
    {
        using var document = JsonDocument.Parse(await content.ReadAsByteArrayAsync());
        return document.RootElement.Clone();
    }
}

/// <summary>An image worker whose answer the test chooses: by default, the bytes it was given.</summary>
internal sealed class FakeImages : AetherFrame.Server.Images.IImageProcessor
{
    public Func<byte[], byte[]?> Answer { get; set; } = bytes => bytes;

    public bool Busy { get; set; }

    public Task<AetherFrame.Server.Images.ImageProcessing> ProcessAsync(AetherFrame.Protocol.Remote.ImageReference declared, ReadOnlyMemory<byte> bytes, CancellationToken cancellation)
    {
        if (Busy)
        {
            return Task.FromResult(AetherFrame.Server.Images.ImageProcessing.Busy);
        }

        var answer = Answer(bytes.ToArray());
        return Task.FromResult(answer is null ? AetherFrame.Server.Images.ImageProcessing.Refused : AetherFrame.Server.Images.ImageProcessing.Recoded(answer));
    }
}

/// <summary>A clock the tests move by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// A Lodestone that answers from a table, and counts what it was asked. A status of 0 is a failed
/// connection, and <see cref="Stall"/> sends the headers and then nothing.
/// </summary>
internal sealed class FakeLodestone
{
    public const int Stall = -1;

    public ConcurrentDictionary<long, LodestoneResponse> Pages { get; } = new();

    public ConcurrentQueue<long> Fetched { get; } = new();

    public ConcurrentQueue<Uri> Addresses { get; } = new();
}

/// <summary>The fake Lodestone's connection, which the real client sends its requests through.</summary>
internal sealed class FakeLodestoneHandler(FakeLodestone lodestone) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var address = request.RequestUri!;
        lodestone.Addresses.Enqueue(address);
        var id = long.Parse(address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1], System.Globalization.CultureInfo.InvariantCulture);
        lodestone.Fetched.Enqueue(id);
        var page = lodestone.Pages.TryGetValue(id, out var found) ? found : new LodestoneResponse(404, LodestoneHtml.NotFoundPage);
        if (page.Status == 0)
        {
            throw new HttpRequestException("connection refused");
        }

        if (page.Status == FakeLodestone.Stall)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
        }

        var response = new HttpResponseMessage((HttpStatusCode)page.Status);
        if (page.Html is not null)
        {
            response.Content = new StringContent(page.Html, Encoding.UTF8, "text/html");
        }

        if (page.Status is >= 300 and < 400)
        {
            response.Headers.Location = new Uri("https://example.com/elsewhere");
        }

        return Task.FromResult(response);
    }

    /// <summary>A body that never arrives.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Pages shaped like the Lodestone's (checked against the live site's structure on 2026-09-30), with made-up characters.</summary>
internal static class LodestoneHtml
{
    public const string NotFoundPage = """
        <!DOCTYPE html><html><head><title>FINAL FANTASY XIV, The Lodestone</title></head>
        <body><div class="error__bg"><div class="error__body"><h2 class="error__heading">Page not found</h2><p class="error__text">The page you are looking for could not be found.</p></div></div></body></html>
        """;

    public static LodestoneResponse Character(string name, string world, string introduction, string dataCenter = "Aether", string sidebar = "AF-0000000000") => new(200, $"""
        <!DOCTYPE html><html><head><title>{name} | FINAL FANTASY XIV, The Lodestone</title></head>
        <body>
        <div class="ldst__side"><h3>Recent Activity</h3><p>Other players' blog: {sidebar}</p></div>
        <div class="frame__chara js__toggle_wrapper"><a class="frame__chara__link state_btn" href="#"><div class="frame__chara__face"></div>
        <div class="frame__chara__box"><p class="frame__chara__title">The Adventurer</p><p class="frame__chara__name">{name}</p>
        <p class="frame__chara__world"><i class="xiv-lds xiv-lds-home-world js__tooltip" data-tooltip="Home World"></i>{world} [{dataCenter}]</p></div></a></div>
        <div class="character__selfintroduction">{introduction}<br></div>
        </body></html>
        """);
}

/// <summary>Every line the server logs, formatted, with its category.</summary>
internal sealed class CapturedLog : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    public string All => string.Join("\n", Lines);

    private sealed class Logger(CapturedLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            log.Lines.Enqueue(category + " scope: " + state);
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            log.Lines.Enqueue($"{category} {logLevel}: {formatter(state, exception)} {exception}");
        }
    }
}
