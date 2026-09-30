using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Personas.Storage;
using AetherFrame.Services.Network.Personas;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// The plugin's persona session (decisions K3, P3 and L12), with its outside world faked: it
/// probes, then locks, then loads, then audits, off the calling thread; it retries a lock held
/// elsewhere and never a failed probe; it runs one operation at a time; and it releases the lock
/// exactly once, as its own last work ends, before that work's unload registration ends. Its log
/// names states and error kinds, never a path or an exception's text, and nothing after it closes.
/// </summary>
public sealed class PersonaSessionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // A path-like text an exception might carry: no log line may ever repeat it.
    private static readonly string SecretPath = Path.Combine("C:", "Users", "SomeoneSecret", "AppData");

    private readonly List<string> events = new();
    private readonly List<string> log = new();
    private readonly InMemoryPersonaKeyStore store = new();
    private readonly InMemoryRegistryStorage registry = new();
    private readonly List<TimeSpan> delays = new();
    private readonly CancellationTokenSource stopping = new();
    private Func<PersonaCapabilities> probe = () => PersonaCapabilities.All;
    private Func<PersonaLockOutcome> lockOutcome = () => PersonaLockOutcome.Acquired;
    private bool shuttingDown;
    private int probes;
    private int leasesOpen;
    private Action<string>? reports;

    [Fact]
    public async Task Start_ProbesThenLocksThenLoadsThenAudits_AndIsReady()
    {
        var session = NewSession();
        Assert.True(session.Start());
        Assert.False(session.Start());
        await Settled(session);

        Assert.Equal(PersonaSessionState.Ready, session.View.State);
        Assert.NotNull(session.View.Audit);
        Assert.Equal(new[] { "probe", "lock", "key store", "registry", "Read" }, events.Where(e => e is "probe" or "lock" or "key store" or "registry" or "Read"));
        Assert.Contains(log, line => line.StartsWith("Personas: ready", StringComparison.Ordinal));
        Assert.Equal(0, leasesOpen);
        Assert.DoesNotContain("lock released", events);
    }

    [Fact]
    public async Task AFailedProbe_TurnsPersonasOffForTheSession_WithoutTheLockOrARetry()
    {
        probe = () => PersonaCapabilities.Without(PersonaCapability.KeyProtection, "the protector claims no protection");
        var session = NewSession();
        session.Start();
        await Settled(session);

        Assert.Equal(PersonaSessionState.Unavailable, session.View.State);
        Assert.Equal(PersonaUnavailableReason.NotOnThisSystem, session.View.Reason);
        Assert.StartsWith("Personas aren't available on this system yet", session.View.Message, StringComparison.Ordinal);
        Assert.False(session.View.CanRetry);
        Assert.False(session.Retry());
        Assert.DoesNotContain("lock", events);
        Assert.Equal(1, probes);
        Assert.False(session.Capabilities!.CanUsePersonas);
        Assert.True(session.Capabilities.CanView);
    }

    [Fact]
    public async Task AProbeThatThrows_CountsAsOneThatFoundNothing()
    {
        probe = () => throw new InvalidOperationException("probe bug at " + SecretPath);
        var session = NewSession();
        session.Start();
        await Settled(session);

        Assert.Equal(PersonaUnavailableReason.NotOnThisSystem, session.View.Reason);
        Assert.False(session.View.CanRetry);
        Assert.DoesNotContain("lock", events);
        AssertLogIsClean();
    }

    [Fact]
    public async Task ALockHeldElsewhere_IsRetriedWithBackoff_ThenOffWithTryAgain_AndTheRetryReusesTheProbe()
    {
        lockOutcome = () => PersonaLockOutcome.HeldElsewhere;
        var session = NewSession();
        session.Start();
        await Settled(session);

        Assert.Equal(PersonaUnavailableReason.InUseElsewhere, session.View.Reason);
        Assert.True(session.View.CanRetry);
        Assert.True(delays.Count > 3);
        Assert.True(delays.Zip(delays.Skip(1)).All(pair => pair.Second >= pair.First));
        Assert.True(delays.Aggregate(TimeSpan.Zero, (sum, d) => sum + d) >= TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("registry", events);

        lockOutcome = () => PersonaLockOutcome.Acquired;
        Assert.True(session.Retry());
        await Settled(session);
        Assert.Equal(PersonaSessionState.Ready, session.View.State);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task ALockFreedDuringTheRetries_IsTaken()
    {
        var attempts = 0;
        lockOutcome = () => ++attempts < 3 ? PersonaLockOutcome.HeldElsewhere : PersonaLockOutcome.Acquired;
        var session = NewSession();
        session.Start();
        await Settled(session);

        Assert.Equal(PersonaSessionState.Ready, session.View.State);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task AnUnusableFolder_IsOffAtOnce_WithTryAgain()
    {
        lockOutcome = () => PersonaLockOutcome.Unusable;
        var session = NewSession();
        session.Start();
        await Settled(session);

        Assert.Equal(PersonaUnavailableReason.FolderUnusable, session.View.Reason);
        Assert.True(session.View.CanRetry);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task AnUnreadableRegistry_IsLeftAsItWas_TheLockIsReleased_AndTryAgainLoadsIt()
    {
        registry.ReadFailure = new IOException("sharing violation at " + SecretPath);
        var session = NewSession();
        session.Start();
        await Settled(session);

        Assert.Equal(PersonaUnavailableReason.RegistryUnreadable, session.View.Reason);
        Assert.True(session.View.CanRetry);
        Assert.Contains("lock released", events);
        Assert.DoesNotContain("Replace", registry.Calls);
        AssertLogIsClean();

        registry.ReadFailure = null;
        Assert.True(session.Retry());
        await Settled(session);
        Assert.Equal(PersonaSessionState.Ready, session.View.State);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task ARegistryANewerAetherFrameWrote_IsToldApart()
    {
        registry.Bytes = [.. "AFPR"u8, 0, 2];
        var session = NewSession();
        session.Start();
        await Settled(session);

        Assert.Equal(PersonaUnavailableReason.RegistryNewerVersion, session.View.Reason);
        Assert.Contains("Update AetherFrame", session.View.Message, StringComparison.Ordinal);
        Assert.Contains("lock released", events);
    }

    [Fact]
    public async Task Operations_RunOneAtATime_OffTheCallingThread_AndReportTheirOutcome()
    {
        var session = await ReadySession();
        using var release = new ManualResetEventSlim(false);
        var callingThread = Environment.CurrentManagedThreadId;
        var ranOn = 0;
        Assert.True(session.TryStart("create", manager =>
        {
            ranOn = Environment.CurrentManagedThreadId;
            release.Wait(Patience);
            manager.Create("Main");
            return PersonaOperationOutcome.Done(manager.Audit());
        }));

        Assert.True(session.View.Busy);
        Assert.False(session.TryStart("create", manager => PersonaOperationOutcome.Done()));
        release.Set();
        await Settled(session);

        Assert.NotEqual(callingThread, ranOn);
        Assert.True(session.View.LastOutcome!.Succeeded);
        Assert.Equal("Main", Assert.Single(session.Personas).Label);
        Assert.Equal(0, leasesOpen);
    }

    [Fact]
    public async Task ARunThatKeepsItsOwnResult_GoesOneAtATime_AndLeavesTheLastOutcomeAsItWas()
    {
        var session = await ReadySession();

        // An operation the persona window started, and the outcome it waits to take.
        Assert.True(session.TryStart("create", manager => PersonaOperationOutcome.Done(persona: manager.Create("Main"))));
        await Settled(session);
        var outcome = session.View.LastOutcome;
        Assert.True(outcome!.Succeeded);

        // A run for another window (publishing, say): one at a time with every other operation,
        // off the calling thread, and it never replaces that outcome, even when it throws.
        using var release = new ManualResetEventSlim(false);
        var callingThread = Environment.CurrentManagedThreadId;
        var ranOn = 0;
        Assert.True(session.TryRun("publish", _ =>
        {
            ranOn = Environment.CurrentManagedThreadId;
            release.Wait(Patience);
        }));
        Assert.True(session.View.Busy);
        Assert.False(session.TryRun("publish", _ => { }));
        Assert.False(session.TryStart("create", _ => PersonaOperationOutcome.Done()));
        release.Set();
        await Settled(session);
        Assert.NotEqual(callingThread, ranOn);
        Assert.Same(outcome, session.View.LastOutcome);

        Assert.True(session.TryRun("publish", _ => throw new IOException("disk full at " + SecretPath)));
        await Settled(session);
        Assert.Same(outcome, session.View.LastOutcome);
        Assert.Contains(log, line => line.StartsWith("Personas: publish failed: IOException 0x", StringComparison.Ordinal));
        AssertLogIsClean();
        Assert.Equal(0, leasesOpen);
    }

    [Fact]
    public async Task AFailedOperation_ReportsItsError_AndTheLogNamesKindsOnly()
    {
        var session = await ReadySession();
        registry.FailNextReplace = new IOException("disk full at " + SecretPath);
        Assert.True(session.TryStart("create", manager =>
        {
            manager.Create("Main");
            return PersonaOperationOutcome.Done();
        }));
        await Settled(session);

        Assert.False(session.View.LastOutcome!.Succeeded);
        Assert.Equal(PersonaError.RegistryWriteFailed, session.View.LastOutcome.Error);
        Assert.Contains(log, line => line.StartsWith("Personas: create failed: PersonaException(RegistryWriteFailed)", StringComparison.Ordinal) && line.Contains("IOException 0x", StringComparison.Ordinal));
        AssertLogIsClean();
    }

    [Fact]
    public async Task AFailure_AuditsTheKeyFilesAgain_OnlyWhenAsked_SoAKeptKeyShowsAtOnce()
    {
        var session = await ReadySession();
        var before = session.View.Audit;
        registry.FailNextReplace = new IOException("disk full at " + SecretPath);
        Assert.True(session.TryStart("create", manager =>
        {
            manager.Create("Main");
            return PersonaOperationOutcome.Done();
        }));
        await Settled(session);

        // Not asked: the view keeps the audit it had.
        Assert.Null(session.View.LastOutcome!.Audit);
        Assert.Same(before, session.View.Audit);

        registry.FailNextReplace = new IOException("disk full at " + SecretPath);
        Assert.True(session.TryStart(
            "create",
            manager =>
            {
                manager.Create("Alt");
                return PersonaOperationOutcome.Done();
            },
            auditAfterFailure: true));
        await Settled(session);

        // Asked: the keys both failed saves kept show as keys without a persona, in the outcome and the view.
        var failed = session.View.LastOutcome!;
        Assert.False(failed.Succeeded);
        Assert.Equal(PersonaError.RegistryWriteFailed, failed.Error);
        Assert.Equal(2, failed.Audit!.Orphans.Count);
        Assert.Same(failed.Audit, session.View.Audit);
        Assert.Empty(session.Personas);
        AssertLogIsClean();
    }

    [Fact]
    public async Task NothingStarts_WhenUnloadingHasBegun_OrBeforeTheSessionIsReady()
    {
        shuttingDown = true;
        var session = NewSession();
        Assert.False(session.Start());
        Assert.Empty(events);

        shuttingDown = false;
        Assert.False(session.TryStart("create", manager => PersonaOperationOutcome.Done()));
        Assert.True(session.Start());
        await Settled(session);
        shuttingDown = true;
        Assert.False(session.TryStart("create", manager => PersonaOperationOutcome.Done()));
        Assert.Equal(0, leasesOpen);
    }

    [Fact]
    public async Task Close_WhenIdle_ReleasesTheLockAtOnce_AndNothingStartsAfter()
    {
        var session = await ReadySession();
        session.Close();
        session.Close();

        Assert.Equal(PersonaSessionState.Closed, session.View.State);
        Assert.Equal(1, events.Count(e => e == "lock released"));
        Assert.False(session.TryStart("create", manager => PersonaOperationOutcome.Done()));
        Assert.False(session.Retry());
        Assert.Empty(session.Personas);
    }

    [Fact]
    public async Task Close_DuringAnOperation_LeavesTheLockToIt_ThenItsLeaseEnds_AndNothingIsLogged()
    {
        var session = await ReadySession();
        var leasesEndedBefore = events.Count(e => e == "lease ended");
        using var release = new ManualResetEventSlim(false);
        session.TryStart("create", manager =>
        {
            release.Wait(Patience);
            throw new IOException("late failure at " + SecretPath);
        });

        var linesBefore = log.Count;
        session.Close();
        Assert.DoesNotContain("lock released", events);

        release.Set();
        await WaitFor(() => leasesOpen == 0);
        Assert.Equal(new[] { "lock released", "lease ended" }, events.Where(e => e is "lock released" or "lease ended").TakeLast(2));
        Assert.Equal(1, events.Count(e => e == "lock released"));
        Assert.Equal(leasesEndedBefore + 1, events.Count(e => e == "lease ended"));
        Assert.Equal(linesBefore, log.Count);
        Assert.Equal(PersonaSessionState.Closed, session.View.State);
        Assert.False(session.View.Busy);
    }

    [Fact]
    public async Task Close_DuringTheStart_ReleasesALockTakenAfterIt_AndPublishesNothing()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        lockOutcome = () =>
        {
            entered.Set();
            release.Wait(Patience);
            return PersonaLockOutcome.Acquired;
        };

        var session = NewSession();
        session.Start();
        Assert.True(entered.Wait(Patience));
        session.Close();
        release.Set();
        await WaitFor(() => leasesOpen == 0);

        Assert.Equal(PersonaSessionState.Closed, session.View.State);
        Assert.Equal(1, events.Count(e => e == "lock released"));
        Assert.DoesNotContain("registry", events);
        Assert.Equal(new[] { "lock released", "lease ended" }, events.Where(e => e is "lock released" or "lease ended").TakeLast(2));
    }

    [Fact]
    public async Task Unloading_StopsAWaitForTheLock()
    {
        lockOutcome = () => PersonaLockOutcome.HeldElsewhere;
        var session = NewSession(delay: async (wait, token) =>
        {
            stopping.Cancel();
            await Task.Delay(Timeout.Infinite, token);
        });

        session.Start();
        await WaitFor(() => leasesOpen == 0 && !session.View.Busy);
        Assert.Equal(1, events.Count(e => e == "lock"));
        Assert.DoesNotContain("registry", events);
        Assert.DoesNotContain(log, line => line.Contains("couldn't start", StringComparison.Ordinal));
        Assert.DoesNotContain(log, line => line.Contains("held elsewhere", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Close_DuringTheProbe_StopsTheStartBeforeTheLock()
    {
        // A probe slowed by a scanner can outlast unloading's wait: once the session is closed, the
        // start takes no lock and creates nothing, whenever the probe returns.
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        probe = () =>
        {
            entered.Set();
            release.Wait(Patience);
            return PersonaCapabilities.All;
        };

        var session = NewSession();
        session.Start();
        Assert.True(entered.Wait(Patience));
        session.Close();
        release.Set();
        await WaitFor(() => leasesOpen == 0);

        Assert.DoesNotContain("lock", events);
        Assert.DoesNotContain("lock released", events);
        Assert.Equal(PersonaSessionState.Closed, session.View.State);
        Assert.False(session.View.Busy);
    }

    [Fact]
    public async Task AProbeThatThrows_LeavesAResultThatViewsNothing()
    {
        probe = () => throw new InvalidOperationException("probe bug");
        var session = NewSession();
        session.Start();
        await Settled(session);

        Assert.NotNull(session.Capabilities);
        Assert.False(session.Capabilities!.CanView);
        Assert.False(session.Capabilities.CanUsePersonas);
    }

    [Fact]
    public async Task TheKeyStoresReports_GoThroughTheSessionsLog_AndStopWhenItCloses()
    {
        var session = await ReadySession();
        Assert.NotNull(reports);
        reports!("The key under a slot is unavailable: no key is held.");
        Assert.Contains(log, line => line.EndsWith("no key is held.", StringComparison.Ordinal));

        session.Close();
        var count = log.Count;
        reports("after close");
        Assert.Equal(count, log.Count);
    }

    [Fact]
    public void Describe_NamesTypesErrorsAndHResults_NeverText()
    {
        var described = PersonaSession.Describe(new PersonaException(PersonaError.RegistryWriteFailed, "outer at " + SecretPath, new IOException("inner at " + SecretPath, unchecked((int)0x80070020))));
        Assert.Equal("PersonaException(RegistryWriteFailed) 0x80131500 <- IOException 0x80070020", described);
    }

    private PersonaSession NewSession(Func<TimeSpan, CancellationToken, Task>? delay = null) => new(new PersonaSessionSeams
    {
        Probe = () =>
        {
            Record("probe");
            probes++;
            return probe();
        },
        AcquireLock = () =>
        {
            Record("lock");
            var outcome = lockOutcome();
            return (outcome, outcome == PersonaLockOutcome.Acquired ? new Recorder(this, "lock released") : null);
        },
        OpenKeyStore = report =>
        {
            Record("key store");
            reports = report;
            return store;
        },
        OpenRegistry = () =>
        {
            Record("registry");
            return new RecordingRegistry(this, registry);
        },
        BeginOperation = () =>
        {
            if (shuttingDown)
            {
                return null;
            }

            Interlocked.Increment(ref leasesOpen);
            return new Recorder(this, "lease ended", () => Interlocked.Decrement(ref leasesOpen));
        },
        Stopping = stopping.Token,
        Log = line =>
        {
            lock (log)
            {
                log.Add(line);
            }
        },
        Delay = delay ?? ((wait, token) =>
        {
            lock (delays)
            {
                delays.Add(wait);
            }

            return Task.CompletedTask;
        }),
    });

    private async Task<PersonaSession> ReadySession()
    {
        var session = NewSession();
        session.Start();
        await Settled(session);
        Assert.Equal(PersonaSessionState.Ready, session.View.State);
        return session;
    }

    private void Record(string what)
    {
        lock (events)
        {
            events.Add(what);
        }
    }

    private void AssertLogIsClean()
    {
        lock (log)
        {
            Assert.All(log, line => Assert.DoesNotContain("SomeoneSecret", line, StringComparison.Ordinal));
            Assert.All(log, line => Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), line, StringComparison.Ordinal));
        }
    }

    private async Task Settled(PersonaSession session) => await WaitFor(() => !session.View.Busy && leasesOpen == 0);

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the session did not settle in time");
            await Task.Delay(5);
        }
    }

    /// <summary>Records every <see cref="Dispose"/>, a second one included, so "released exactly once" is checked rather than assumed.</summary>
    private sealed class Recorder(PersonaSessionTests owner, string what, Action? then = null) : IDisposable
    {
        public void Dispose()
        {
            owner.Record(what);
            then?.Invoke();
        }
    }

    private sealed class RecordingRegistry(PersonaSessionTests owner, InMemoryRegistryStorage inner) : IPersonaRegistryStorage
    {
        public byte[]? Read()
        {
            owner.Record("Read");
            return inner.Read();
        }

        public void Replace(ReadOnlySpan<byte> bytes) => inner.Replace(bytes);
    }
}
