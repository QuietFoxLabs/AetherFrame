using System;
using System.IO;
using System.Threading;

namespace AetherFrame.Services.Network.Personas;

/// <summary>What happened when the persona files' lock was asked for.</summary>
public enum PersonaLockOutcome
{
    /// <summary>The lock is held, by this session, until it is disposed.</summary>
    Acquired,

    /// <summary>Something else holds it: another game client on this Windows account, a plugin instance still unloading, or a scanner that opened the file.</summary>
    HeldElsewhere,

    /// <summary>The persona directory or its lock file can't be used at all (access denied, a file where the directory should be).</summary>
    Unusable,
}

/// <summary>
/// The persona files' single-writer lock (docs/networking/DecisionRegister.md, P3):
/// <c>instance.lock</c> in the networking directory, held open with no sharing for the session, so
/// that one game client at a time uses this Windows account's personas. The session takes it before
/// the registry is read and releases it only by <see cref="Dispose"/>, never to a finalizer: Dalamud
/// reloads plugins in the same process, and a lock left to the finalizer would keep the reloaded
/// plugin out until the game restarts. The file holds nothing and is never deleted. Compiled only in
/// the networking preview flavour.
/// </summary>
public sealed class PersonaInstanceLock : IDisposable
{
    /// <summary>The lock file's name in the networking directory.</summary>
    public const string LockName = "instance.lock";

    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    private FileStream? stream;

    private PersonaInstanceLock(FileStream stream)
    {
        this.stream = stream;
    }

    /// <summary>
    /// Takes the lock in <paramref name="directory"/>, creating the directory and the file when they
    /// are missing. One attempt: the session retries <see cref="PersonaLockOutcome.HeldElsewhere"/>.
    /// It never throws for the file system; <see cref="PersonaLockOutcome.Unusable"/> says so.
    /// </summary>
    public static PersonaLockOutcome TryAcquire(string directory, out PersonaInstanceLock? held)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        held = null;
        try
        {
            Directory.CreateDirectory(directory);
            var opened = new FileStream(Path.Combine(directory, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            held = new PersonaInstanceLock(opened);
            return PersonaLockOutcome.Acquired;
        }
        catch (IOException e) when (IsHeldElsewhere(e))
        {
            return PersonaLockOutcome.HeldElsewhere;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return PersonaLockOutcome.Unusable;
        }
    }

    /// <summary>Releases the lock, once; later calls do nothing.</summary>
    public void Dispose() => Interlocked.Exchange(ref stream, null)?.Dispose();

    // Windows reports another holder as a sharing or lock violation. Other systems emulate the
    // sharing mode with an advisory lock and report it differently; the plugin runs on Windows and
    // under Wine, where these codes hold, so elsewhere a held lock reads as unusable.
    private static bool IsHeldElsewhere(IOException e) =>
        OperatingSystem.IsWindows() && (e.HResult & 0xFFFF) is SharingViolation or LockViolation;
}
