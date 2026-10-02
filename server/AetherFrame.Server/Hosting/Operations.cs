using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Server.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AetherFrame.Server.Hosting;

/// <summary>
/// The backup (decision D1; N2-8): a consistent copy of the database once a UTC day, written with
/// <c>VACUUM INTO</c>, and deleted within <see cref="Retention"/> of its writing, so a deletion has
/// reached every copy once that time has passed. The consent text quotes the retention. A run every
/// <see cref="Interval"/> writes the day's copy if it isn't there yet and deletes the copies due, so
/// neither a restart nor a failed run moves a deletion later. Each run's outcome is
/// <c>/v1/health</c>'s <c>backup</c> (<see cref="ServerHealth"/>).
/// </summary>
internal sealed class Backups(IOptions<ServerOptions> options, ServerDatabase database, ServerHealth health, TimeProvider time, ILogger<Backups> logger) : BackgroundService
{
    /// <summary>How long a backup is kept, at most.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>
    /// How long after one run the next starts, whether it succeeded or not. A run writes only the
    /// copy its UTC day lacks, so there is still one copy a day, and a failed run is simply tried
    /// again at the next.
    /// </summary>
    internal TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(options.Value.BackupFolder))
        {
            return;
        }

        // A timer, not a pause after each run, so the runs stay an interval apart however long a copy
        // takes: the sweep's margin relies on it.
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("A backup failed with {ErrorKind}.", e.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Writes today's backup if there is none, then deletes those due, whether or not the copy
    /// succeeded, and records the outcome: a success only when both did. A copy of day D is due once
    /// <see cref="Retention"/> less two <see cref="Interval"/>s has passed since D began, so a run in
    /// the second-last interval before D plus the retention deletes it, or, after a restart shorter
    /// than an interval, a run in the last. It was written on day D, so it is gone within the
    /// retention of its writing, whatever the runs' phase, and whether or not a run failed.
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken cancellation)
    {
        try
        {
            await CopyAndSweepAsync(cancellation);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            health.BackupFinished(succeeded: false);
            throw;
        }

        health.BackupFinished(succeeded: true);
    }

    private async Task CopyAndSweepAsync(CancellationToken cancellation)
    {
        var folder = options.Value.BackupFolder;
        var now = time.GetUtcNow();
        var due = Retention - (2 * Interval);
        var today = Path.Combine(folder, "server-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".db");
        ExceptionDispatchInfo? copyFailure = null;
        if (!File.Exists(today))
        {
            try
            {
                await CopyAsync(today, cancellation);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                copyFailure = ExceptionDispatchInfo.Capture(e);
            }
        }

        // The sweep runs even when the copy failed, so that while copies fail (a full disk, say),
        // none older than the retention survives either.
        foreach (var file in Directory.GetFiles(folder, "server-*"))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".partial", StringComparison.Ordinal) && !name.StartsWith(Path.GetFileName(today), StringComparison.Ordinal))
            {
                File.Delete(file);
                continue;
            }

            if (name.Length >= "server-yyyyMMdd".Length
                && DateTime.TryParseExact(name.Substring("server-".Length, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day)
                && now.UtcDateTime - day >= due)
            {
                File.Delete(file);
            }
        }

        copyFailure?.Throw();
    }

    /// <summary>Copies the database to <paramref name="today"/>, through a partial file that a failure never leaves behind.</summary>
    private async Task CopyAsync(string today, CancellationToken cancellation)
    {
        var partial = today + ".partial";
        File.Delete(partial);
        try
        {
            await using (var connection = await database.OpenAsync(cancellation))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "VACUUM INTO $path;";
                command.Parameters.AddWithValue("$path", partial);
                await command.ExecuteNonQueryAsync(cancellation);
            }

            // VACUUM INTO closes the copy when its statement ends, so no connection holds it open and it
            // moves even on Windows. No pool is cleared: clearing them all (known bug 12) closed every
            // pooled connection in the process, and could close one that a request had just opened.
            File.Move(partial, today);
        }
        catch
        {
            // A failed copy (a full disk, say) is never left to outlive the retention.
            File.Delete(partial);
            throw;
        }
    }
}

/// <summary>
/// The operator's commands (docs/networking/Runbook.md), run in the server's container:
/// <c>dotnet AetherFrame.Server.dll admin &lt;command&gt;</c>. They work on the database directly,
/// with the same deletions and checkpoints as the server's own requests.
/// </summary>
internal static class AdminCommands
{
    public const string Usage = """
        Usage: dotnet AetherFrame.Server.dll admin <command>
          characters                  every bound character: Lodestone id, name, World, and whether it is shown
          reports                     every report: number, day, reported Lodestone id, reason
          resolve-report <number>     deletes a report the operator has dealt with
          remove-character <id>       deletes a character's binding and everything published for it (S3, C4)
          allowlist                   the Lodestone ids the configuration allows now (C8)
        """;

    public static async Task<int> RunAsync(string[] args, ServerDatabase database, BindingStore bindings, TextWriter output, IReadOnlyList<string>? allowlist = null, bool open = false)
    {
        switch (args)
        {
            case ["allowlist"]:
                if (open)
                {
                    await output.WriteLineAsync("Open to everyone (OpenToEveryone): any character whose Lodestone check passes may share and view. The ids below don't limit who.");
                }

                foreach (var text in allowlist ?? [])
                {
                    await output.WriteLineAsync(Lodestone.LodestoneIds.TryParse(text, out _) ? text : text + "  (not a Lodestone id: ignored)");
                }

                await output.WriteLineAsync((allowlist?.Count ?? 0) == 1 ? "1 id." : (allowlist?.Count ?? 0) + " ids.");
                if (allowlist?.Any(text => !Lodestone.LodestoneIds.TryParse(text, out _)) == true)
                {
                    await output.WriteLineAsync("An entry isn't a Lodestone id, so the server allows no one until the file is fixed.");
                    return 1;
                }

                return 0;
        }

        await database.InitializeAsync(CancellationToken.None);
        switch (args)
        {
            case ["characters"]:
                await ListAsync(database, "SELECT lodestone_id, name, world, CASE hidden WHEN 0 THEN 'shown' ELSE 'hidden' END FROM bindings ORDER BY lodestone_id;", output);
                return 0;
            case ["reports"]:
                await ListAsync(database, "SELECT id, date(day * 86400, 'unixepoch'), lodestone_id, reason FROM reports ORDER BY id;", output);
                return 0;
            case ["resolve-report", var number] when long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var id):
                {
                    await using var connection = await database.OpenAsync(CancellationToken.None);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "DELETE FROM reports WHERE id = $id;";
                    command.Parameters.AddWithValue("$id", id);
                    var deleted = await command.ExecuteNonQueryAsync();
                    await output.WriteLineAsync(deleted == 1 ? "Report deleted." : "No such report.");
                    return deleted == 1 ? 0 : 1;
                }

            case ["remove-character", var text] when Lodestone.LodestoneIds.TryParse(text, out var lodestoneId):
                {
                    var removed = await bindings.RemoveCharacterAsync(lodestoneId, CancellationToken.None);
                    if (!removed)
                    {
                        await output.WriteLineAsync("No character has that Lodestone id.");
                        return 1;
                    }

                    // The deletion is committed; the checkpoint that clears it from the write-ahead log must finish too.
                    if (database.CheckpointOwed && !await database.CheckpointAsync(CancellationToken.None))
                    {
                        await output.WriteLineAsync("The character is deleted, but readers kept its pages in the write-ahead log. Run the command again, or restart the server, to clear them.");
                        return 3;
                    }

                    await output.WriteLineAsync("The character and everything published for it are deleted.");
                    return 0;
                }

            default:
                await output.WriteLineAsync(Usage);
                return 2;
        }
    }

    private static async Task ListAsync(ServerDatabase database, string sql, TextWriter output)
    {
        await using var connection = await database.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            var fields = new List<string>();
            for (var index = 0; index < reader.FieldCount; index++)
            {
                fields.Add(Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "");
            }

            await output.WriteLineAsync(string.Join("  ", fields));
            rows++;
        }

        await output.WriteLineAsync(rows == 1 ? "1 row." : rows + " rows.");
    }
}
