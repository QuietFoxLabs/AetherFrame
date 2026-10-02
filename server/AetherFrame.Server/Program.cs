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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var reloaded = false;

// The operator's configuration file (N2-8): the allowlist lives here, and edits to it take effect as
// soon as it reloads, which the deployment's polling watcher makes a few seconds (decision C8).
if (Environment.GetEnvironmentVariable("AETHERFRAME_CONFIG_FILE") is { Length: > 0 } configFile)
{
    var fileSource = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
    {
        Path = configFile,
        Optional = false,
        ReloadOnChange = true,
        OnLoadException = failure =>
        {
            // At start a bad file stops the server. On a reload, .NET drops the file's values, so the
            // allowlist is empty, and allows no one, until the file is fixed; the log says so (the
            // file itself is never logged).
            if (reloaded)
            {
                failure.Ignore = true;
                Console.Error.WriteLine("The configuration file can't be read: the allowlist is empty until it is fixed.");
            }
        },
    };
    fileSource.ResolveFileProvider();

    // Before the environment variables, so the file can't override what the deployment sets there.
    var environment = builder.Configuration.Sources.ToList().FindIndex(source => source is Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationSource);
    builder.Configuration.Sources.Insert(environment < 0 ? builder.Configuration.Sources.Count : environment, fileSource);
    reloaded = true;
}

ServerOptions.RefuseForwardedHeadersSwitch(builder.Configuration);

// The operator's commands (docs/networking/Runbook.md) run instead of the server.
if (args is ["admin", .. var command])
{
    var adminOptions = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();

    // The allowlist command shows the file's entries as they are, even ones that make the options
    // invalid, so the operator can see what to fix; every other command needs valid options.
    if (command is not ["allowlist"])
    {
        adminOptions.Validate();
    }

    var adminDatabase = new ServerDatabase(Options.Create(adminOptions), Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerDatabase>.Instance);
    return await AdminCommands.RunAsync(command, adminDatabase, new BindingStore(adminDatabase, TimeProvider.System), Console.Out, adminOptions.AllowedLodestoneIds, adminOptions.IsOpen);
}

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
builder.Services.AddSingleton<ServerHealth>();
builder.Services.AddSingleton<ImageWorkerClient>();
builder.Services.AddHostedService(services => services.GetRequiredService<ImageWorkerClient>());
builder.Services.AddSingleton<IImageProcessor>(services =>
    services.GetRequiredService<IOptions<ServerOptions>>().Value is { ImageWorkerSocket: "", ImageWorkerRuns: "" }
        ? new NoImageProcessor()
        : services.GetRequiredService<ImageWorkerClient>());
builder.Services.AddHostedService<DatabaseStartup>();
builder.Services.AddHostedService<CheckpointRetries>();
builder.Services.AddHostedService<Housekeeping>();
builder.Services.AddHostedService<Backups>();
builder.Services.AddHostedService(services => services.GetRequiredService<Rereads>());
builder.Services.AddSingleton<ImageCanary>();
builder.Services.AddHostedService(services => services.GetRequiredService<ImageCanary>());
builder.Services.AddSingleton<HealthWatch>();
builder.Services.AddHostedService(services => services.GetRequiredService<HealthWatch>());
builder.Services.AddHttpClient(LodestoneHttpPages.ClientName, (services, client) =>
    {
        var options = services.GetRequiredService<IOptions<ServerOptions>>().Value;
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AetherFrame-Server/1 (+https://{options.DeploymentName}/)");
    })
    .ConfigurePrimaryHttpMessageHandler(services => LodestoneHttpPages.CreateHandler(services.GetRequiredService<IOptions<ServerOptions>>().Value.Relay))
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
HealthEndpoints.Map(app);
await app.RunAsync();
return 0;

/// <summary>The entry point, named so the tests' host can start it.</summary>
public partial class Program;
