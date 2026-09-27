using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Persistence;
using Xunit;

namespace AetherFrame.Tests;

/// <summary>
/// One dedicated thread draining a queue of work, like Dalamud's framework-thread task scheduler:
/// each item is STARTED on that thread and never waited for there, so an async method's
/// synchronous prefix runs on the dispatcher thread and whatever follows an incomplete await runs
/// wherever the awaited task completes.
/// </summary>
internal sealed class QueuedDispatcher : IDisposable
{
    private readonly BlockingCollection<(Func<Task> Work, TaskCompletionSource Completion)> queue = new();
    private readonly Thread thread;

    internal QueuedDispatcher()
    {
        thread = new Thread(Drain) { IsBackground = true, Name = "QueuedDispatcher" };
        thread.Start();
    }

    /// <summary>The managed id of the dispatcher thread.</summary>
    internal int ThreadId => thread.ManagedThreadId;

    /// <summary>Queues <paramref name="work"/>. The returned Task completes (or faults) when the
    /// work's own Task does, however many threads that takes.</summary>
    internal Task Dispatch(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add((work, completion));
        return completion.Task;
    }

    /// <summary>Lets the thread finish what is already queued, then stops it. Called from the
    /// dispatcher thread itself, it only stops the queue; the thread ends after the current item.</summary>
    public void Dispose()
    {
        queue.CompleteAdding();
        if (Environment.CurrentManagedThreadId == thread.ManagedThreadId)
        {
            return;
        }

        thread.Join();
        queue.Dispose();
    }

    private void Drain()
    {
        foreach (var (work, completion) in queue.GetConsumingEnumerable())
        {
            Task started;
            try
            {
                started = work();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
                continue;
            }

            // Not awaited here: a dispatcher thread that waited on its work would be a deadlock
            // in the game whenever the work needs a later tick of the same thread.
            started.ContinueWith(
                static (finished, state) =>
                {
                    var completion = (TaskCompletionSource)state!;
                    if (finished.IsFaulted)
                    {
                        completion.SetException(finished.Exception!.InnerExceptions);
                    }
                    else if (finished.IsCanceled)
                    {
                        completion.SetCanceled();
                    }
                    else
                    {
                        completion.SetResult();
                    }
                },
                completion,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}

/// <summary>Forwards every call to another store, recording which thread each call was made on.</summary>
internal sealed class ThreadRecordingStore : IPlateFileStore
{
    private readonly IPlateFileStore inner;
    private readonly ConcurrentQueue<(string Operation, string Path, int ThreadId)> calls = new();

    internal ThreadRecordingStore(IPlateFileStore inner)
    {
        this.inner = inner;
    }

    /// <summary>Every call so far, in order: the member, the (source) path, and the managed id of
    /// the thread that made the call.</summary>
    internal IReadOnlyList<(string Operation, string Path, int ThreadId)> Calls => calls.ToArray();

    public bool FileExists(string path)
    {
        Record(nameof(FileExists), path);
        return inner.FileExists(path);
    }

    public IReadOnlyList<string> ListFiles(string directory, string searchPattern)
    {
        Record(nameof(ListFiles), directory);
        return inner.ListFiles(directory, searchPattern);
    }

    public Task ReadTextAsync(string path, Action<string> reader)
    {
        Record(nameof(ReadTextAsync), path);
        return inner.ReadTextAsync(path, reader);
    }

    public Task WriteTextAsync(string path, string contents)
    {
        Record(nameof(WriteTextAsync), path);
        return inner.WriteTextAsync(path, contents);
    }

    public void MoveFile(string sourcePath, string destinationPath)
    {
        Record(nameof(MoveFile), sourcePath);
        inner.MoveFile(sourcePath, destinationPath);
    }

    public void CopyFile(string sourcePath, string destinationPath)
    {
        Record(nameof(CopyFile), sourcePath);
        inner.CopyFile(sourcePath, destinationPath);
    }

    public void DeleteFile(string path)
    {
        Record(nameof(DeleteFile), path);
        inner.DeleteFile(path);
    }

    private void Record(string operation, string path) => calls.Enqueue((operation, path, Environment.CurrentManagedThreadId));
}

public class QueuedDispatcherTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RunsTheSynchronousPrefixOnItsThread_AndTheContinuationElsewhere()
    {
        using var dispatcher = new QueuedDispatcher();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefixThread = -1;
        var continuationThread = -1;

        var dispatched = dispatcher.Dispatch(async () =>
        {
            prefixThread = Environment.CurrentManagedThreadId;
            await release.Task;
            continuationThread = Environment.CurrentManagedThreadId;
        });

        // The dispatcher thread moves on to the next item while the first is still awaiting.
        await dispatcher.Dispatch(() => Task.CompletedTask).WaitAsync(Generous);
        Assert.Equal(dispatcher.ThreadId, prefixThread);
        Assert.False(dispatched.IsCompleted);

        release.SetResult();
        await dispatched.WaitAsync(Generous);
        Assert.NotEqual(-1, continuationThread);
        Assert.NotEqual(dispatcher.ThreadId, continuationThread);
    }
}
