using System;
using System.Threading.Tasks;
using AetherFrame.Services.Diagnostics;
using AetherFrame.Services.Plates;
using AetherFrame.Services.Templates;

namespace AetherFrame.UI.Library;

/// <summary>
/// Runs a window's Plate and Template Library operations one at a time without blocking its
/// drawing: each runs as a task, and its outcome is applied on the render thread by the next
/// <see cref="Advance"/>. My Plates and each editor have one, so a result or an error shows in
/// the window where the player asked for it.
///
/// <para>A refusal meant for the player (a <see cref="PlateLibraryException"/> or a
/// <see cref="TemplateLibraryException"/>) becomes <see cref="Error"/> in its own words. Any other
/// failure is logged the moment it happens, not when the window next draws (which may be never,
/// if it was closed meanwhile), and <see cref="Error"/> says only that it failed. Starting an
/// operation clears both messages.</para>
/// </summary>
internal sealed class PlateOperationRunner
{
    /// <summary>What <see cref="Error"/> says when an operation is asked for while one is running.</summary>
    internal const string BusyMessage = "Please wait for the current action to finish.";

    private readonly IAetherFrameLog log;

    private Task<Action?>? operation;
    private string operationName = string.Empty;
    private string? error;
    private string? status;

    internal PlateOperationRunner(IAetherFrameLog log) => this.log = log;

    /// <summary>An operation is running (its outcome not applied yet).</summary>
    internal bool IsBusy => operation is not null;

    /// <summary>
    /// Completes once the running operation has finished, successfully or not; its outcome still
    /// waits for <see cref="Advance"/>. Completed when nothing is running.
    /// </summary>
    internal Task Finished => operation is { } running ? running.ContinueWith(static _ => { }, TaskScheduler.Default) : Task.CompletedTask;

    /// <summary>The last failure or refusal to show, if any; its host may also set or clear it.</summary>
    internal string? Error
    {
        get => error;
        set
        {
            error = value;
            MessageVersion++;
        }
    }

    /// <summary>The last result to show, if any; its host may also set or clear it.</summary>
    internal string? Status
    {
        get => status;
        set
        {
            status = value;
            MessageVersion++;
        }
    }

    /// <summary>Changes whenever <see cref="Error"/> or <see cref="Status"/> is set, so a window can tell a new message from one it has shown.</summary>
    internal int MessageVersion { get; private set; }

    /// <summary>What <see cref="Error"/> says when <paramref name="name"/> failed unexpectedly.</summary>
    internal static string FailedMessage(string name) => $"Couldn't {name}. See the Dalamud log for details.";

    /// <summary>Starts <paramref name="work"/>, named as in "Couldn't {name}"; <paramref name="onSuccess"/> runs on the render thread.</summary>
    internal void Run(string name, Func<Task> work, Action? onSuccess = null) =>
        Start(name, async () =>
        {
            await work().ConfigureAwait(false);
            return onSuccess;
        });

    /// <summary>Starts <paramref name="work"/>; <paramref name="onSuccess"/> gets its result on the render thread.</summary>
    internal void Run<T>(string name, Func<Task<T>> work, Action<T> onSuccess) =>
        Start(name, async () =>
        {
            var result = await work().ConfigureAwait(false);
            return () => onSuccess(result);
        });

    /// <summary>Applies a finished operation's result or error. Call once per frame, on the render thread.</summary>
    internal void Advance()
    {
        if (operation is not { IsCompleted: true } task)
        {
            return;
        }

        operation = null;

        if (task.IsCompletedSuccessfully)
        {
            task.Result?.Invoke();
            return;
        }

        Error = task.Exception?.GetBaseException() switch
        {
            PlateLibraryException refused => refused.Message,
            TemplateLibraryException refused => refused.Message,
            _ => FailedMessage(operationName),
        };
    }

    private void Start(string name, Func<Task<Action?>> work)
    {
        if (IsBusy)
        {
            Error = BusyMessage;
            return;
        }

        Error = null;
        Status = null;
        operationName = name;
        operation = RunLoggedAsync(name, work);
    }

    private async Task<Action?> RunLoggedAsync(string name, Func<Task<Action?>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not PlateLibraryException and not TemplateLibraryException)
        {
            log.Error(ex, $"AetherFrame failed to {name}.");
            throw;
        }
    }
}
