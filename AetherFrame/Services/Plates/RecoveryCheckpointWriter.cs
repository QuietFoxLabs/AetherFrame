using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Domain.Plates;
using AetherFrame.Services.Diagnostics;

namespace AetherFrame.Services.Plates;

/// <summary>What finished on the recovery writer (see <see cref="RecoveryCheckpointWriter"/>).</summary>
internal sealed record RecoveryWriteResult(long RequestId, Guid PlateId, Guid EditId, bool IsRetire, bool Succeeded, Exception? Error);

/// <summary>
/// Runs recovery checkpoint writes and retirements one at a time, away from the framework thread, in
/// the order they were asked for (see <see cref="RecoveryCheckpointStore"/>). At most one runs, and at
/// most one checkpoint per editing waits: a newer one replaces a waiting older one, which is then
/// never written, so an older write can never land after a newer checkpoint was asked for. Retiring an
/// editing drops its waiting checkpoint and runs after any write of it already under way, so a write
/// finishing late can never bring back what was saved or discarded. Each outcome is reported through
/// the callback, on the writer's thread. Safe from any thread.
/// </summary>
internal sealed class RecoveryCheckpointWriter
{
    private readonly RecoveryCheckpointStore store;
    private readonly Action<RecoveryWriteResult> completed;
    private readonly IAetherFrameLog log;
    private readonly object gate = new();
    private readonly LinkedList<Operation> waiting = new();

    private Task running = Task.CompletedTask;
    private bool busy;
    private bool stopped;
    private long nextRequestId;

    internal RecoveryCheckpointWriter(RecoveryCheckpointStore store, Action<RecoveryWriteResult> completed, IAetherFrameLog? log = null)
    {
        this.store = store;
        this.completed = completed;
        this.log = log ?? NullAetherFrameLog.Instance;
    }

    internal RecoveryCheckpointStore Store => store;

    /// <summary>Operations waiting, not counting the one running: never more than one checkpoint per editing.</summary>
    internal int WaitingCount
    {
        get { lock (gate) return waiting.Count; }
    }

    /// <summary>
    /// Asks for <paramref name="checkpoint"/> to be written; returns its request id, or null once
    /// stopped. A checkpoint of the same editing still waiting is replaced by this one.
    /// </summary>
    internal long? Write(PlateDraft checkpoint)
    {
        var editId = checkpoint.EditId ?? throw new ArgumentException("A checkpoint names its editing.", nameof(checkpoint));
        lock (gate)
        {
            if (stopped)
            {
                return null;
            }

            RemoveWaitingWrites(editId);
            var id = ++nextRequestId;
            waiting.AddLast(new Operation(id, checkpoint.PlateId, editId, checkpoint));
            StartLocked();
            return id;
        }
    }

    /// <summary>
    /// Asks for every checkpoint of one editing to be deleted, after anything of it already being
    /// written; a checkpoint of it still waiting is dropped. Accepted even once stopped, so a save or
    /// discard just before unloading is still honoured.
    /// </summary>
    internal long Retire(Guid plateId, Guid editId)
    {
        lock (gate)
        {
            RemoveWaitingWrites(editId);
            foreach (var node in Nodes().Where(n => n.Value.Checkpoint is null && n.Value.EditId == editId).ToList())
            {
                waiting.Remove(node);
            }

            var id = ++nextRequestId;
            waiting.AddLast(new Operation(id, plateId, editId, null));
            StartLocked();
            return id;
        }
    }

    /// <summary>
    /// No new checkpoints. What was asked for still runs, at most one waiting write per editing (a
    /// final checkpoint of unsaved changes left behind among them), and retirements.
    /// </summary>
    internal void Stop()
    {
        lock (gate)
        {
            stopped = true;
        }
    }

    /// <summary>Completes once nothing runs or waits, or after <paramref name="timeout"/>; true when idle.</summary>
    internal async Task<bool> WaitIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task current;
            lock (gate)
            {
                if (!busy && waiting.Count == 0)
                {
                    return true;
                }

                current = running;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.WhenAny(current, Task.Delay(left)).ConfigureAwait(false);
        }
    }

    private IEnumerable<LinkedListNode<Operation>> Nodes()
    {
        for (var node = waiting.First; node is not null; node = node.Next)
        {
            yield return node;
        }
    }

    private void RemoveWaitingWrites(Guid editId)
    {
        foreach (var node in Nodes().Where(n => n.Value.Checkpoint is not null && n.Value.EditId == editId).ToList())
        {
            waiting.Remove(node);
        }
    }

    private void StartLocked()
    {
        if (busy)
        {
            return;
        }

        busy = true;
        running = Task.Run(RunAsync);
    }

    private Task RunAsync()
    {
        while (true)
        {
            Operation operation;
            lock (gate)
            {
                if (waiting.First is not { } first)
                {
                    busy = false;
                    return Task.CompletedTask;
                }

                operation = first.Value;
                waiting.RemoveFirst();
            }

            Exception? error = null;
            try
            {
                if (operation.Checkpoint is { } checkpoint)
                {
                    store.Write(checkpoint);
                }
                else
                {
                    store.Retire(operation.PlateId, operation.EditId);
                }
            }
            catch (Exception ex)
            {
                error = ex;
                log.Error(ex, operation.Checkpoint is null
                    ? $"AetherFrame couldn't remove the recovery checkpoints of Plate {operation.PlateId}; it tries again."
                    : $"AetherFrame couldn't write a recovery checkpoint of Plate {operation.PlateId}; it tries again.");
            }

            try
            {
                completed(new RecoveryWriteResult(operation.Id, operation.PlateId, operation.EditId, operation.Checkpoint is null, error is null, error));
            }
            catch (Exception ex)
            {
                log.Error(ex, "AetherFrame couldn't report a recovery checkpoint's outcome.");
            }
        }
    }

    private sealed record Operation(long Id, Guid PlateId, Guid EditId, PlateDraft? Checkpoint);
}
