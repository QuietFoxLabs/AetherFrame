using System;
using System.Linq;
using System.Net;
using AetherFrame.Server;
using AetherFrame.Server.Endpoints;
using AetherFrame.Server.Hosting;
using AetherFrame.Server.Images;
using AetherFrame.Server.Limits;
using AetherFrame.Server.Lodestone;
using AetherFrame.Server.Requests;
using AetherFrame.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
ServerOptions.RefuseForwardedHeadersSwitch(builder.Configuration);

// Decision S5: nothing that logs a request's URL, address or headers. ASP.NET Core's hosting
// diagnostics log each request at Information, and HttpClient's handlers log each URL, which holds a
// Lodestone id: both stay at Warning, and the Lodestone client has no logging handler at all.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = SignedRequests.MaxActionRequestBytes;
});

builder.Services.AddOptions<ServerOptions>()
    .Bind(builder.Configuration.GetSection(ServerOptions.Section))
    .Validate(options =>
    {
        options.Validate();
        return true;
    })
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ServerDatabase>();
builder.Services.AddSingleton<ChallengeStore>();
builder.Services.AddSingleton<BindingStore>();
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddSingleton<SignedRequests>();
builder.Services.AddSingleton<Allowlist>();
builder.Services.AddSingleton(services => new Worlds(services.GetRequiredService<IOptions<ServerOptions>>().Value.Worlds));
builder.Services.AddSingleton<LodestoneBudget>();
builder.Services.AddSingleton<LodestoneReader>();
builder.Services.AddSingleton<ILodestonePages, LodestoneHttpPages>();
builder.Services.AddSingleton<Rereads>();
builder.Services.AddSingleton<ContentStore>();
builder.Services.AddSingleton<PublishSlots>();
builder.Services.AddSingleton<Viewing>();
builder.Services.AddSingleton<ImageWorkerClient>();
builder.Services.AddHostedService(services => services.GetRequiredService<ImageWorkerClient>());
builder.Services.AddSingleton<IImageProcessor>(services =>
    string.IsNullOrEmpty(services.GetRequiredService<IOptions<ServerOptions>>().Value.ImageWorkerSocket)
        ? new NoImageProcessor()
        : services.GetRequiredService<ImageWorkerClient>());
builder.Services.AddHostedService<DatabaseStartup>();
builder.Services.AddHostedService<CheckpointRetries>();
builder.Services.AddHostedService<Housekeeping>();
builder.Services.AddHostedService(services => services.GetRequiredService<Rereads>());
builder.Services.AddHttpClient(LodestoneHttpPages.ClientName, (services, client) =>
    {
        var options = services.GetRequiredService<IOptions<ServerOptions>>().Value;
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AetherFrame-Server/1 (+https://{options.DeploymentName}/)");
    })
    .ConfigurePrimaryHttpMessageHandler(LodestoneHttpPages.CreateHandler)
    .RemoveAllLoggers();

builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    // Decision R4: only the reverse proxy's forwarded header is believed, and by default only
    // loopback is trusted; the trusted lists are never cleared.
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    forwarded.ForwardLimit = 1;
});
builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<ServerOptions>>((forwarded, options) =>
{
    foreach (var proxy in options.Value.KnownProxies.Select(IPAddress.Parse))
    {
        forwarded.KnownProxies.Add(proxy);
    }
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseMiddleware<RequestLog>();
CharacterEndpoints.Map(app);
PlateEndpoints.Map(app);
app.Run();

/// <summary>The entry point, named so the tests' host can start it.</summary>
public partial class Program;
