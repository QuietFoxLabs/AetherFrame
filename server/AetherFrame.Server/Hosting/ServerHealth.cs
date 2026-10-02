using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AetherFrame.Server.Hosting;

/// <summary>
/// What <c>GET /v1/health</c> answers (ServerApi-v1.md, section 3; known bug 14): whether image
/// worker runs are connecting, whether an image goes through the worker and passes the server's
/// check, and whether the hourly backup run keeps the day's copy and deletes the old ones. Each is
/// true or false and nothing more: no count, time, version or identifier. It lives in memory,
/// written by the worker client, the image canary and the backup as they finish, and a request only
/// reads it.
/// </summary>
internal sealed class ServerHealth
{
    /// <summary>
    /// How long after a worker run's connection the worker counts as connecting. The server's start
    /// gives it no grace: a healthy host connects a run within seconds of the first socket offered.
    /// </summary>
    public static readonly TimeSpan WorkerWindow = TimeSpan.FromMinutes(2);

    /// <summary>How long after the last good canary images count as going through.</summary>
    public static readonly TimeSpan ImagesWindow = TimeSpan.FromHours(3);

    /// <summary>
    /// How long after the last good backup run the backup counts as working: three of its hourly
    /// runs, so a loop that stopped shows within 3 hours. A run that failed shows at once.
    /// </summary>
    public static readonly TimeSpan BackupWindow = TimeSpan.FromHours(3);

    /// <summary>How long after the server's start images or the backup, with nothing finished yet, counts as healthy. The worker has no such grace.</summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(15);

    /// <summary>The failed canaries in a row that turn images unhealthy.</summary>
    public const int CanaryFailuresInARow = 2;

    private readonly object gate = new();
    private readonly TimeProvider time;
    private readonly DateTimeOffset started;
    private readonly bool backupConfigured;

    // UTC ticks of the last worker connection, 0 for none: written by the worker client's accept loop.
    private long lastWorkerConnection;
    private DateTimeOffset? lastCanarySuccess;
    private int canaryFailures;
    private DateTimeOffset? lastBackupSuccess;
    private bool lastBackupFailed;

    public ServerHealth(IOptions<ServerOptions> options, TimeProvider time)
    {
        this.time = time;
        started = time.GetUtcNow();
        WorkerConfigured = !string.IsNullOrEmpty(options.Value.ImageWorkerRuns) || !string.IsNullOrEmpty(options.Value.ImageWorkerSocket);
        backupConfigured = !string.IsNullOrEmpty(options.Value.BackupFolder);
    }

    /// <summary>Whether an image worker is configured at all: without one, the worker and images are never healthy.</summary>
    public bool WorkerConfigured { get; }

    /// <summary>The three signals now.</summary>
    public HealthAnswer Current => Evaluate(time.GetUtcNow(), State);

    /// <summary>What the signals are judged from, at this moment.</summary>
    internal HealthState State
    {
        get
        {
            var connection = Interlocked.Read(ref lastWorkerConnection);
            lock (gate)
            {
                return new HealthState(
                    started,
                    WorkerConfigured,
                    connection == 0 ? null : new DateTimeOffset(connection, TimeSpan.Zero),
                    lastCanarySuccess,
                    canaryFailures,
                    backupConfigured,
                    lastBackupSuccess,
                    lastBackupFailed);
            }
        }
    }

    /// <summary>A worker run has just connected.</summary>
    public void WorkerConnected() => Interlocked.Exchange(ref lastWorkerConnection, time.GetUtcNow().UtcTicks);

    /// <summary>A canary finished: its image came back and passed, or it didn't. A busy worker is no finish.</summary>
    public void CanaryFinished(bool succeeded)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            if (succeeded)
            {
                lastCanarySuccess = now;
                canaryFailures = 0;
            }
            else
            {
                canaryFailures = Math.Min(canaryFailures + 1, CanaryFailuresInARow);
            }
        }
    }

    /// <summary>A backup run finished: the day's copy is in place and the retention sweep is done, or either failed.</summary>
    public void BackupFinished(bool succeeded)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            lastBackupFailed = !succeeded;
            if (succeeded)
            {
                lastBackupSuccess = now;
            }
        }
    }

    /// <summary>
    /// The three signals at <paramref name="now"/>. The worker is healthy within
    /// <see cref="WorkerWindow"/> of a run's last connection, and not before a run has connected: the
    /// server's start gives it no grace. Images are healthy within <see cref="ImagesWindow"/> of the
    /// last good canary, unless the last <see cref="CanaryFailuresInARow"/> failed. The backup is
    /// healthy within <see cref="BackupWindow"/> of the last good run, unless the last run failed.
    /// Before images or the backup has succeeded once, each is healthy for the
    /// <see cref="StartGrace"/>. Neither the worker nor images is ever healthy with no worker
    /// configured, nor the backup with no folder.
    /// </summary>
    internal static HealthAnswer Evaluate(DateTimeOffset now, HealthState state) =>
        new(
            state.WorkerConfigured && state.LastWorkerConnection is { } connection && now - connection < WorkerWindow,
            state.WorkerConfigured && state.CanaryFailures < CanaryFailuresInARow && Recent(now, state.LastCanarySuccess, ImagesWindow, state.Started),
            state.BackupConfigured && !state.LastBackupFailed && Recent(now, state.LastBackupSuccess, BackupWindow, state.Started));

    private static bool Recent(DateTimeOffset now, DateTimeOffset? lastSuccess, TimeSpan window, DateTimeOffset started) =>
        lastSuccess is { } success ? now - success < window : now - started < StartGrace;
}

/// <summary>What <see cref="ServerHealth.Evaluate"/> judges from.</summary>
internal readonly record struct HealthState(
    DateTimeOffset Started,
    bool WorkerConfigured,
    DateTimeOffset? LastWorkerConnection,
    DateTimeOffset? LastCanarySuccess,
    int CanaryFailures,
    bool BackupConfigured,
    DateTimeOffset? LastBackupSuccess,
    bool LastBackupFailed);

/// <summary><c>GET /v1/health</c>'s answer: exactly these three booleans.</summary>
internal sealed record HealthAnswer(bool Worker, bool Images, bool Backup);

/// <summary>
/// Looks at the three signals once a minute and logs a warning whenever one changes, by its kind
/// alone, never per request and never with an identifier. The first look is a minute after the
/// start, not at it: the worker reads unhealthy until a run connects, a few seconds in, so a normal
/// start logs nothing. Each starts as healthy, so one still unhealthy at the first look (a backup
/// folder left unset, or no run connected, say) is logged once too.
/// </summary>
internal sealed class HealthWatch(ServerHealth health, TimeProvider time, ILogger<HealthWatch> logger) : BackgroundService
{
    /// <summary>How often the signals are looked at, and how long after the start the first look is.</summary>
    internal TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    private readonly object gate = new();
    private HealthAnswer last = new(true, true, true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, time, stoppingToken);
            CheckOnce();
        }
    }

    /// <summary>Looks once, and logs each signal that changed since the last look. Internal, so tests can drive it without the timer.</summary>
    internal void CheckOnce()
    {
        lock (gate)
        {
            var now = health.Current;
            Report("the image worker", last.Worker, now.Worker);
            Report("image processing", last.Images, now.Images);
            Report("the backup", last.Backup, now.Backup);
            last = now;
        }
    }

    private void Report(string signal, bool was, bool now)
    {
        if (was == now)
        {
            return;
        }

        if (now)
        {
            logger.LogWarning("Server health: {Signal} turned healthy again.", signal);
        }
        else
        {
            logger.LogWarning("Server health: {Signal} turned unhealthy.", signal);
        }
    }
}
