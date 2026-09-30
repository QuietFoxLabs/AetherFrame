using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Personas;
using AetherFrame.Personas.Storage;
using AetherFrame.Protocol.Identity;

namespace AetherFrame.Services.Network.Personas;

/// <summary>Where a persona session stands.</summary>
public enum PersonaSessionState
{
    /// <summary>The capability probe, the lock, the registry and the first audit are under way.</summary>
    Starting,

    /// <summary>Persona features are off for now; <see cref="PersonaSessionView.Reason"/> says why.</summary>
    Unavailable,

    /// <summary>The personas are loaded, and operations can run.</summary>
    Ready,

    /// <summary>The plugin is unloading: nothing new starts.</summary>
    Closed,
}

/// <summary>Why persona features are off.</summary>
public enum PersonaUnavailableReason
{
    /// <summary>Nothing is off.</summary>
    None,

    /// <summary>The capability probe failed (K3): off for the whole session, with no retry.</summary>
    NotOnThisSystem,

    /// <summary>Another holder kept the persona files' lock through every retry: a second game client on this Windows account, or a scanner.</summary>
    InUseElsewhere,

    /// <summary>The persona folder or its lock file can't be used.</summary>
    FolderUnusable,

    /// <summary>The persona registry exists but can't be read; it is left as it was (P3).</summary>
    RegistryUnreadable,

    /// <summary>The persona registry names a later version; it is left as it was (P3).</summary>
    RegistryNewerVersion,

    /// <summary>Something else stopped the start; the log names its kind.</summary>
    Failed,
}

/// <summary>What the persona window reads each frame: one immutable value, replaced whole after each change.</summary>
public sealed class PersonaSessionView
{
    internal PersonaSessionView(PersonaSessionState state, PersonaUnavailableReason reason, string? message, bool canRetry, bool busy, PersonaAudit? audit, PersonaOperationOutcome? lastOutcome)
    {
        State = state;
        Reason = reason;
        Message = message;
        CanRetry = canRetry;
        Busy = busy;
        Audit = audit;
        LastOutcome = lastOutcome;
    }

    /// <summary>Where the session stands.</summary>
    public PersonaSessionState State { get; }

    /// <summary>Why persona features are off, when <see cref="State"/> is <see cref="PersonaSessionState.Unavailable"/>.</summary>
    public PersonaUnavailableReason Reason { get; }

    /// <summary>The one message for the player while persona features are off; null otherwise.</summary>
    public string? Message { get; }

    /// <summary>Whether the player may try again: never after the probe failed (K3).</summary>
    public bool CanRetry { get; }

    /// <summary>Whether the start or an operation is running; the window starts nothing meanwhile.</summary>
    public bool Busy { get; }

    /// <summary>The latest audit of the key files (L12): at start, and after an operation that returned one.</summary>
    public PersonaAudit? Audit { get; }

    /// <summary>What the latest operation came to.</summary>
    public PersonaOperationOutcome? LastOutcome { get; }

    internal PersonaSessionView With(bool busy, PersonaOperationOutcome? lastOutcome, PersonaAudit? audit) =>
        new(State, Reason, Message, CanRetry, busy, audit ?? Audit, lastOutcome ?? LastOutcome);
}

/// <summary>What one persona operation came to, for the window. It holds no path and no exception text.</summary>
public sealed class PersonaOperationOutcome
{
    private PersonaOperationOutcome(bool succeeded, PersonaError? error, PersonaAudit? audit, PersonaRecord? persona, PersonaPublicKey? opened)
    {
        Succeeded = succeeded;
        Error = error;
        Audit = audit;
        Persona = persona;
        Opened = opened;
    }

    /// <summary>Whether it did what it set out to do.</summary>
    public bool Succeeded { get; }

    /// <summary>Why it didn't, when the manager said why; null for success and for an unexpected failure.</summary>
    public PersonaError? Error { get; }

    /// <summary>An audit taken by the operation, which replaces the session's.</summary>
    public PersonaAudit? Audit { get; }

    /// <summary>The persona the operation made or changed, when it did.</summary>
    public PersonaRecord? Persona { get; }

    /// <summary>For a check of a key without a persona: the public key it opened as, or null when it didn't open.</summary>
    public PersonaPublicKey? Opened { get; }

    /// <summary>It worked. <paramref name="audit"/> replaces the session's when it is given.</summary>
    public static PersonaOperationOutcome Done(PersonaAudit? audit = null, PersonaRecord? persona = null, PersonaPublicKey? opened = null) =>
        new(true, null, audit, persona, opened);

    /// <summary>It didn't: <paramref name="error"/> when the manager said why, with an audit taken after the failure when there is one.</summary>
    public static PersonaOperationOutcome Failed(PersonaError? error, PersonaAudit? audit = null) => new(false, error, audit, null, null);
}

/// <summary>
/// Everything a <see cref="PersonaSession"/> needs from outside it, so the persona suite can drive
/// it with fakes and the plugin with the real files, protector, log and unload tracking.
/// </summary>
public sealed class PersonaSessionSeams
{
    /// <summary>The capability probe (K3). It runs at most once per session.</summary>
    public required Func<PersonaCapabilities> Probe { get; init; }

    /// <summary>One attempt at the persona files' lock (P3).</summary>
    public required Func<(PersonaLockOutcome Outcome, IDisposable? Held)> AcquireLock { get; init; }

    /// <summary>
    /// The key store over the key files, made once the lock is held. It is given the session's own
    /// log for its report lines, which, like every session line, writes nothing once the session
    /// is closed.
    /// </summary>
    public required Func<Action<string>, IPersonaKeyStore> OpenKeyStore { get; init; }

    /// <summary>The registry storage, made once the lock is held.</summary>
    public required Func<IPersonaRegistryStorage> OpenRegistry { get; init; }

    /// <summary>Registers one operation with the plugin's unload tracking; null once unloading began, and then nothing starts.</summary>
    public required Func<IDisposable?> BeginOperation { get; init; }

    /// <summary>Signaled when unloading begins: a wait for the lock gives up.</summary>
    public required CancellationToken Stopping { get; init; }

    /// <summary>
    /// Where the session's lines go. They name states, operations and error kinds, never a label,
    /// an identity, a path or an exception's text. Nothing is written once the session is closed.
    /// </summary>
    public required Action<string> Log { get; init; }

    /// <summary>The wait between attempts at the lock.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>How long the start keeps trying the lock while something else holds it.</summary>
    public TimeSpan LockRetryBudget { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The plugin's persona session (docs/networking/DecisionRegister.md, K3, P3, L10 and L12):
/// it starts once, off the framework thread, running the capability probe, then taking the persona
/// files' lock, then loading the registry, then auditing the key files. Afterwards it runs the
/// player's persona operations one at a time, off the framework thread too. The window reads
/// <see cref="View"/>, <see cref="Personas"/> and <see cref="Active"/>, which never wait, and hands
/// every change to <see cref="TryStart"/>. It never holds the manager out, so nothing can call it
/// on the framework thread. Compiled only in the networking preview flavour.
/// <para>
/// The lock is taken before the registry is read, and the manager is made only with it held. It
/// is released by exactly one party, decided under the session's monitor: <see cref="Close"/> when
/// nothing of the session's runs, or else the start or operation in flight, as it ends. It is
/// released before that work's unload registration ends, and never at a timeout: a reloaded plugin
/// that starts meanwhile finds it held, retries, and at worst stays off with a message to try
/// again. Nothing is left to a finalizer. The start and every operation register with the plugin's
/// unload tracking, so unloading waits for them within its own budget. A start stops at its next
/// step once the session is closed: nothing new is probed or created, and no new attempt at the
/// lock is made; an attempt already under way when it closes is dropped at once. An operation
/// still running when the budget ends runs to its end, since stopping it between a key's commit
/// and the registry save would only leave an orphan; unlike the plugin's other owned operations,
/// it doesn't stop before its next file step.
/// </para>
/// </summary>
public sealed class PersonaSession
{
    private const string ProbeFailedMessage = "Personas aren't available on this system yet: AetherFrame couldn't check what works here.";

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LongestRetryDelay = TimeSpan.FromSeconds(2);

    private readonly PersonaSessionSeams seams;
    private readonly object gate = new();
    private IDisposable? heldLock;
    private volatile PersonaManager? manager;
    private volatile PersonaCapabilities? capabilities;
    private volatile PersonaSessionView view = new(PersonaSessionState.Starting, PersonaUnavailableReason.None, null, false, false, null, null);
    private volatile bool closed;
    private bool busy;
    private bool started;

    /// <summary>A session over <paramref name="seams"/>, not started yet.</summary>
    public PersonaSession(PersonaSessionSeams seams)
    {
        ArgumentNullException.ThrowIfNull(seams);
        this.seams = seams;
    }

    /// <summary>Where the session stands, for the window.</summary>
    public PersonaSessionView View => view;

    /// <summary>The personas, from the manager's snapshot; empty until the session is ready.</summary>
    public IReadOnlyList<PersonaRecord> Personas => manager?.Personas ?? Array.Empty<PersonaRecord>();

    /// <summary>The active persona, from the manager's snapshot.</summary>
    public PersonaRecord? Active => manager?.Active;

    /// <summary>
    /// What the capability probe found, once it ran: viewing (N2-10) needs its verification step even
    /// when personas are off. A probe that threw leaves a result that views nothing; its missing
    /// capability reads as signature verification only because nothing at all was checked, so N2-10
    /// must not report it as a failure specific to signatures.
    /// </summary>
    public PersonaCapabilities? Capabilities => capabilities;

    /// <summary>Begins the start in the background, once. False when it already began, or the plugin is unloading.</summary>
    public bool Start() => Begin(retry: false);

    /// <summary>
    /// Begins the start again after it ended unavailable for a reason that allows it: never after
    /// the probe failed, whose result holds for the whole session (K3). The probe does not run again.
    /// </summary>
    public bool Retry() => Begin(retry: true);

    /// <summary>
    /// Runs <paramref name="work"/> on the manager in the background, when the session is ready and
    /// nothing else runs; false otherwise, and then nothing starts. <paramref name="name"/> names the
    /// operation in the log, so it is a fixed word such as "create", never a label. When the work
    /// throws, the session logs its kind and, with <paramref name="auditAfterFailure"/>, audits the
    /// key files again, so a key a failed save kept shows at once.
    /// </summary>
    public bool TryStart(string name, Func<PersonaManager, PersonaOperationOutcome> work, bool auditAfterFailure = false)
    {
        ArgumentNullException.ThrowIfNull(work);
        return StartOperation(name, work, auditAfterFailure, record: true);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the manager as <see cref="TryStart"/> does (one operation at a
    /// time, off the framework thread, and waited for by unloading), for a caller that keeps its own
    /// result, such as publishing (N2-6c). The view's last outcome is left as it was, so the persona
    /// window never takes another window's operation for one of its own. When the work throws, the
    /// session logs its kind and leaves the view as it was.
    /// </summary>
    public bool TryRun(string name, Action<PersonaManager> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return StartOperation(name, manager =>
        {
            work(manager);
            return PersonaOperationOutcome.Done();
        }, auditAfterFailure: false, record: false);
    }

    private bool StartOperation(string name, Func<PersonaManager, PersonaOperationOutcome> work, bool auditAfterFailure, bool record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var lease = seams.BeginOperation();
        if (lease is null)
        {
            return false;
        }

        PersonaManager? target;
        lock (gate)
        {
            target = closed || busy || view.State != PersonaSessionState.Ready ? null : manager;
            if (target is not null)
            {
                busy = true;
                view = view.With(busy: true, lastOutcome: null, audit: null);
            }
        }

        if (target is null)
        {
            lease.Dispose();
            return false;
        }

        _ = Task.Run(() => RunOperation(name, target, work, auditAfterFailure, record, lease));
        return true;
    }

    /// <summary>
    /// The plugin is unloading: nothing new starts, and the lock is released now when nothing of the
    /// session's runs, or else by the work in flight as it ends. Never throws; later calls do nothing.
    /// </summary>
    public void Close()
    {
        IDisposable? release = null;
        lock (gate)
        {
            if (closed)
            {
                return;
            }

            closed = true;
            view = new PersonaSessionView(PersonaSessionState.Closed, PersonaUnavailableReason.None, null, false, busy, null, null);
            if (!busy)
            {
                release = heldLock;
                heldLock = null;
                manager = null;
            }
        }

        ReleaseQuietly(release);
    }

    /// <summary>An exception as the log may show it: each type name in the chain, with its HResult and any persona error; never its text, which can hold a path.</summary>
    internal static string Describe(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add(current is PersonaException persona
                ? $"{nameof(PersonaException)}({persona.Error}) 0x{current.HResult:X8}"
                : $"{current.GetType().Name} 0x{current.HResult:X8}");
        }

        return string.Join(" <- ", parts);
    }

    private static string MessageFor(PersonaUnavailableReason reason) => reason switch
    {
        PersonaUnavailableReason.InUseElsewhere => "Personas are in use by another copy of AetherFrame on this Windows account, such as a second game client. Close it, then choose Try again.",
        PersonaUnavailableReason.FolderUnusable => "AetherFrame can't use its persona folder. Check that the folder can be written to, then choose Try again.",
        PersonaUnavailableReason.RegistryUnreadable => "Your persona list couldn't be read, so personas are off for now. It is left exactly as it was.",
        PersonaUnavailableReason.RegistryNewerVersion => "Your persona list was saved by a newer AetherFrame, so personas are off for now. Update AetherFrame to use it; it is left exactly as it was. If AetherFrame is already up to date, the file may be damaged.",
        _ => "Personas couldn't start. Choose Try again; if it happens again, AetherFrame's log names what failed.",
    };

    private static PersonaSessionView Unavailable(PersonaUnavailableReason reason, string message, bool canRetry) =>
        new(PersonaSessionState.Unavailable, reason, message, canRetry, false, null, null);

    private static void ReleaseQuietly(IDisposable? held)
    {
        try
        {
            held?.Dispose();
        }
        catch (Exception)
        {
            // Closing a file handle doesn't fail in practice, and a failure here must never stop
            // the unload or the release of the unload registration that follows.
        }
    }

    private bool Begin(bool retry)
    {
        var lease = seams.BeginOperation();
        if (lease is null)
        {
            return false;
        }

        var begun = false;
        lock (gate)
        {
            var allowed = retry
                ? view.State == PersonaSessionState.Unavailable && view.CanRetry
                : !started;
            if (!closed && !busy && allowed)
            {
                started = true;
                busy = true;
                view = new PersonaSessionView(PersonaSessionState.Starting, PersonaUnavailableReason.None, null, false, true, null, null);
                begun = true;
            }
        }

        if (!begun)
        {
            lease.Dispose();
            return false;
        }

        _ = Task.Run(() => RunStartAsync(lease));
        return true;
    }

    private async Task RunStartAsync(IDisposable lease)
    {
        PersonaSessionView? result = null;
        IDisposable? unkept = null;
        try
        {
            // Unloading stops a start at every step it can: nothing is probed, created or locked once
            // the session is closed. Work already under way (the probe itself) runs to its end.
            if (closed)
            {
                return;
            }

            // The probe runs once per session (K3): a retry reuses its result, and a probe that throws
            // (it never does for a platform failure) counts as one that found nothing, for good.
            PersonaCapabilities found;
            try
            {
                found = capabilities ?? seams.Probe();
            }
            catch (Exception e)
            {
                capabilities = PersonaCapabilities.Without(PersonaCapability.SignatureVerification, "the probe failed: " + Describe(e));
                Log("Personas: off, the capability probe failed: " + Describe(e));
                result = Unavailable(PersonaUnavailableReason.NotOnThisSystem, ProbeFailedMessage, canRetry: false);
                return;
            }

            capabilities = found;
            if (!found.CanUsePersonas)
            {
                Log($"Personas: off on this system ({found.Missing}). {found.Detail}");
                result = Unavailable(PersonaUnavailableReason.NotOnThisSystem, found.Message ?? ProbeFailedMessage, canRetry: false);
                return;
            }

            if (closed)
            {
                return;
            }

            var (outcome, held) = await AcquireLockAsync().ConfigureAwait(false);
            if (outcome != PersonaLockOutcome.Acquired || held is null)
            {
                var reason = outcome == PersonaLockOutcome.HeldElsewhere ? PersonaUnavailableReason.InUseElsewhere : PersonaUnavailableReason.FolderUnusable;
                Log($"Personas: off, the persona folder's lock is {(reason == PersonaUnavailableReason.InUseElsewhere ? "held elsewhere" : "unusable")}.");
                result = Unavailable(reason, MessageFor(reason), canRetry: true);
                return;
            }

            // Kept only while the session is open; a lock taken after Close is released at once.
            lock (gate)
            {
                if (closed)
                {
                    unkept = held;
                    return;
                }

                heldLock = held;
            }

            PersonaManager loaded;
            try
            {
                loaded = PersonaManager.Load(seams.OpenKeyStore(Log), new NoBackupCodec(), seams.OpenRegistry());
            }
            catch (PersonaException e) when (e.Error is PersonaError.RegistryUnreadable or PersonaError.RegistryNewerVersion)
            {
                var reason = e.Error == PersonaError.RegistryNewerVersion ? PersonaUnavailableReason.RegistryNewerVersion : PersonaUnavailableReason.RegistryUnreadable;
                Log("Personas: off, the persona registry was refused: " + Describe(e));
                unkept = TakeLock();
                result = Unavailable(reason, MessageFor(reason), canRetry: true);
                return;
            }

            var audit = loaded.Audit();
            lock (gate)
            {
                if (!closed)
                {
                    manager = loaded;
                }
            }

            Log($"Personas: ready, {loaded.Personas.Count} persona(s), {audit.Orphans.Count} key file(s) without a persona, {audit.Unusable.Count} unusable.");
            result = new PersonaSessionView(PersonaSessionState.Ready, PersonaUnavailableReason.None, null, false, false, audit, null);
        }
        catch (OperationCanceledException) when (seams.Stopping.IsCancellationRequested)
        {
            // Unloading began while the lock was awaited: nothing to report.
        }
        catch (Exception e)
        {
            Log("Personas: couldn't start: " + Describe(e));
            unkept ??= TakeLock();
            result = Unavailable(PersonaUnavailableReason.Failed, MessageFor(PersonaUnavailableReason.Failed), canRetry: true);
        }
        finally
        {
            ReleaseQuietly(unkept);
            Finish(lease, result is null ? null : _ => result);
        }
    }

    private void RunOperation(string name, PersonaManager target, Func<PersonaManager, PersonaOperationOutcome> work, bool auditAfterFailure, bool record, IDisposable lease)
    {
        PersonaOperationOutcome outcome = PersonaOperationOutcome.Failed(null);
        try
        {
            outcome = work(target) ?? PersonaOperationOutcome.Failed(null);
        }
        catch (Exception e)
        {
            Log($"Personas: {name} failed: {Describe(e)}");
            outcome = PersonaOperationOutcome.Failed(e is PersonaException persona ? persona.Error : null, auditAfterFailure ? AuditQuietly(target) : null);
        }
        finally
        {
            // An operation that keeps its own result leaves the view's last outcome as it was.
            Finish(lease, record
                ? current => current.With(busy: false, lastOutcome: outcome, audit: outcome.Audit)
                : current => current.With(busy: false, lastOutcome: null, audit: null));
        }
    }

    /// <summary>
    /// Ends the start or an operation: the view it leaves, then, when the session closed meanwhile,
    /// the lock released, and last the unload registration, so unloading never sees the work end
    /// while the lock is still held.
    /// </summary>
    private void Finish(IDisposable lease, Func<PersonaSessionView, PersonaSessionView>? next)
    {
        IDisposable? release = null;
        try
        {
            lock (gate)
            {
                busy = false;
                if (closed)
                {
                    release = heldLock;
                    heldLock = null;
                    manager = null;
                    view = view.With(busy: false, lastOutcome: null, audit: null);
                }
                else
                {
                    view = next is null ? view.With(busy: false, lastOutcome: null, audit: null) : next(view);
                }
            }
        }
        finally
        {
            ReleaseQuietly(release);
            lease.Dispose();
        }
    }

    /// <summary>An audit after a failure, or none when the audit itself fails; it never throws.</summary>
    private PersonaAudit? AuditQuietly(PersonaManager target)
    {
        try
        {
            return target.Audit();
        }
        catch (Exception e)
        {
            Log("Personas: the audit after a failure failed: " + Describe(e));
            return null;
        }
    }

    /// <summary>The lock this session holds, taken out of it for its own start to release.</summary>
    private IDisposable? TakeLock()
    {
        lock (gate)
        {
            var taken = heldLock;
            heldLock = null;
            return taken;
        }
    }

    /// <summary>
    /// The lock, retried while something else holds it. A closed session makes no further attempt:
    /// it returns without the lock, and its start ends quietly.
    /// </summary>
    private async Task<(PersonaLockOutcome Outcome, IDisposable? Held)> AcquireLockAsync()
    {
        var waited = TimeSpan.Zero;
        var delay = FirstRetryDelay;
        while (!closed)
        {
            var (outcome, held) = seams.AcquireLock();
            if (outcome != PersonaLockOutcome.HeldElsewhere || waited >= seams.LockRetryBudget)
            {
                return (outcome, held);
            }

            await seams.Delay(delay, seams.Stopping).ConfigureAwait(false);
            waited += delay;
            delay = delay * 2 < LongestRetryDelay ? delay * 2 : LongestRetryDelay;
        }

        return (PersonaLockOutcome.HeldElsewhere, null);
    }

    private void Log(string line)
    {
        if (closed)
        {
            return;
        }

        try
        {
            seams.Log(line);
        }
        catch (Exception)
        {
            // The log is Dalamud's, and it may already be gone while the plugin unloads.
        }
    }
}
