using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AetherFrame.Server.Storage;

/// <summary>
/// The server's one SQLite database (decisions D1 and C7). Every connection it opens has
/// <c>secure_delete</c> on, so deleted content doesn't stay in the file, and after each deletion of
/// published content a truncating checkpoint, retried until no reader blocks it, clears the
/// write-ahead log too. Every value reaches SQLite as a parameter, never in a statement's text
/// (decision N7).
/// </summary>
internal sealed class ServerDatabase
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS challenges (
            value BLOB PRIMARY KEY,
            expires INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS codes (
            persona TEXT PRIMARY KEY,
            code TEXT NOT NULL UNIQUE,
            expires INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS bindings (
            persona TEXT PRIMARY KEY,
            lodestone_id INTEGER NOT NULL UNIQUE,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL,
            world TEXT NOT NULL,
            profile_id TEXT NOT NULL UNIQUE,
            hidden INTEGER NOT NULL DEFAULT 0,
            not_found_day INTEGER
        ) WITHOUT ROWID;
        CREATE UNIQUE INDEX IF NOT EXISTS bindings_shown ON bindings (name_key, world) WHERE hidden = 0;
        CREATE TABLE IF NOT EXISTS revisions (
            persona TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            revision_id BLOB NOT NULL,
            sha256 BLOB NOT NULL,
            PRIMARY KEY (persona, profile_id, revision_id)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS latest (
            persona TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            sequence INTEGER NOT NULL,
            revision_id BLOB NOT NULL,
            document BLOB NOT NULL,
            served BLOB NOT NULL,
            marker BLOB NOT NULL,
            PRIMARY KEY (persona, profile_id)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS images (
            persona TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            idx INTEGER NOT NULL,
            format INTEGER NOT NULL,
            bytes BLOB NOT NULL,
            PRIMARY KEY (persona, profile_id, idx)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS taken_over (
            persona TEXT PRIMARY KEY
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS reports (
            id INTEGER PRIMARY KEY,
            lodestone_id INTEGER NOT NULL,
            reason TEXT NOT NULL,
            reporter TEXT NOT NULL,
            day INTEGER NOT NULL
        );
        """;

    private readonly string connectionString;
    private readonly ILogger<ServerDatabase> logger;

    public ServerDatabase(IOptions<ServerOptions> options, ILogger<ServerDatabase> logger)
    {
        connectionString = ConnectionStringFor(options.Value.DatabasePath);
        this.logger = logger;
    }

    /// <summary>
    /// The connection string of the database at <paramref name="path"/>. Microsoft.Data.Sqlite keeps one
    /// pool per connection string, so this text names the server's pool: the tests clear that pool, and
    /// only that one, before they delete the file.
    /// </summary>
    internal static string ConnectionStringFor(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
    }.ToString();

    /// <summary>How long a checkpoint is retried while readers block it, before it is left to <see cref="CheckpointRetries"/>.</summary>
    internal TimeSpan CheckpointPatience { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether a checkpoint gave up and is owed: <see cref="CheckpointRetries"/> runs it until it completes.</summary>
    internal bool CheckpointOwed { get; private set; }

    /// <summary>Creates the tables and switches the file to write-ahead logging. Idempotent.</summary>
    public async Task InitializeAsync(CancellationToken cancellation)
    {
        await using var connection = await OpenAsync(cancellation);
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellation);
        await ExecuteAsync(connection, Schema, cancellation);
    }

    /// <summary>
    /// Opens a connection with this server's settings: <c>secure_delete</c> (per connection, and off
    /// by default), a small <c>journal_size_limit</c>, foreign keys and a busy timeout.
    /// </summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellation)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellation);
            await ExecuteAsync(connection, "PRAGMA secure_delete = ON; PRAGMA journal_size_limit = 0; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;", cancellation);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Runs <c>wal_checkpoint(TRUNCATE)</c> until its first result, "busy", is 0, backing off while
    /// readers block it (decision D1), so deleted pages leave the write-ahead log too. After
    /// <see cref="CheckpointPatience"/> it leaves the checkpoint owed, and <see cref="CheckpointRetries"/>
    /// keeps trying until it completes. Callers pass <see cref="CancellationToken.None"/> once their
    /// deletion has committed, so a client that goes away can't skip it.
    /// </summary>
    public async Task<bool> CheckpointAsync(CancellationToken cancellation)
    {
        try
        {
            return await TryCheckpointAsync(cancellation);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The deletion before it has committed: the checkpoint is owed, and the request succeeds.
            CheckpointOwed = true;
            logger.LogWarning("A truncating checkpoint failed with {ErrorKind}; it is retried.", e.GetType().Name);
            return false;
        }
    }

    private async Task<bool> TryCheckpointAsync(CancellationToken cancellation)
    {
        var delay = TimeSpan.FromMilliseconds(20);
        var deadline = DateTime.UtcNow + CheckpointPatience;
        while (true)
        {
            try
            {
                await using var connection = await OpenAsync(cancellation);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await using var reader = await command.ExecuteReaderAsync(cancellation);
                if (await reader.ReadAsync(cancellation) && reader.GetInt32(0) == 0)
                {
                    CheckpointOwed = false;
                    return true;
                }
            }
            catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6)
            {
                // SQLITE_BUSY or SQLITE_LOCKED as an error rather than a result: busy all the same.
            }

            if (DateTime.UtcNow >= deadline)
            {
                CheckpointOwed = true;
                logger.LogWarning("A truncating checkpoint stayed blocked by readers; it is retried.");
                return false;
            }

            await Task.Delay(delay, cancellation);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
        }
    }

    public static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellation)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellation);
    }
}

/// <summary>Runs an owed checkpoint (decision D1) every 30 seconds until it completes.</summary>
internal sealed class CheckpointRetries(ServerDatabase database) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            if (database.CheckpointOwed)
            {
                try
                {
                    await database.CheckpointAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Still owed: the next round tries again.
                }
            }
        }
    }
}
