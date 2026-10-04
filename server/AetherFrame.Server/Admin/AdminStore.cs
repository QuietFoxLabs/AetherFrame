using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Server.Storage;
using Microsoft.Data.Sqlite;

namespace AetherFrame.Server.Admin;

internal sealed record StaffActor(long Id, string Role, string Generation);
internal sealed record DeskInput
{
    public string Action { get; init; } = "";
    public long Id { get; init; }
    public string Profile { get; init; } = "";
    public string Marker { get; init; } = "";
    public string Token { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool? Held { get; init; }
    public int Page { get; init; }
    public int Image { get; init; }
    public string Filter { get; init; } = "";
}

/// <summary>Staff access, moderation holds and their audit. Every mutation and its access recheck share a write transaction.</summary>
internal sealed class AdminStore(ServerDatabase database, AdminOptions options, TimeProvider clock)
{
    internal static async Task InitializeAsync(SqliteConnection connection, CancellationToken cancellation)
    {
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS admin_staff (
                github_id INTEGER PRIMARY KEY, active INTEGER NOT NULL, generation INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS admin_holds (
                profile_id TEXT PRIMARY KEY REFERENCES bindings(profile_id) ON DELETE CASCADE,
                created INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS admin_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, actor INTEGER NOT NULL,
                action TEXT NOT NULL, profile_id TEXT REFERENCES bindings(profile_id) ON DELETE SET NULL,
                staff_id INTEGER, reason TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS admin_audit_at ON admin_audit(at);
            SELECT COUNT(*) FROM pragma_table_info('reports') WHERE name='review_token';
            """;
        var exists = Convert.ToInt64(await command.ExecuteScalarAsync(cancellation), CultureInfo.InvariantCulture);
        if (exists == 0)
        {
            command.CommandText = "ALTER TABLE reports ADD COLUMN review_token TEXT NOT NULL DEFAULT '';";
            await command.ExecuteNonQueryAsync(cancellation);
        }

        // Also repair rows created by an older binary after a rollback. IDs may be reused by SQLite;
        // the random token distinguishes an old report from a new report with the same row ID.
        command.CommandText = "UPDATE reports SET review_token=lower(hex(randomblob(16))) WHERE review_token='';";
        await command.ExecuteNonQueryAsync(cancellation);
        await transaction.CommitAsync(cancellation);
    }

    public async Task<StaffActor?> FindStaffAsync(long id, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        return await FindStaffAsync(connection, null, id, cancellation);
    }

    private async Task<StaffActor?> FindStaffAsync(SqliteConnection connection, SqliteTransaction? transaction, long id, CancellationToken cancellation)
    {
        if (id <= 0) return null;
        if (id == options.OwnerGitHubId) return new(id, "owner", "owner");
        await using var command = Command(connection, transaction,
            "SELECT generation FROM admin_staff WHERE github_id=$id AND active=1;", ("$id", id));
        var value = await command.ExecuteScalarAsync(cancellation);
        return value is null ? null : new(id, "moderator", Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    public async Task<object> OverviewAsync(CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        var rows = await RowsAsync(connection, """
            SELECT (SELECT count(*) FROM latest) AS published,
                (SELECT count(*) FROM reports WHERE day>$expired) AS reports,
                (SELECT count(*) FROM admin_holds) AS hidden;
            """, cancellation, ("$expired", clock.GetUtcNow().ToUnixTimeSeconds() / 86400 - 30));
        return rows[0];
    }

    public async Task<object> ListAsync(string kind, int page, string filter, CancellationToken cancellation)
    {
        if (page is < 0 or > 10000) throw new ArgumentException("Invalid page.");
        await using var connection = await database.OpenAsync(cancellation);
        var sql = kind switch
        {
            "reports" => """
                SELECT r.id, r.review_token AS token, CAST(r.lodestone_id AS TEXT) AS characterId,
                    r.reason, r.day, b.name, b.world, b.profile_id AS profile,
                    EXISTS(SELECT 1 FROM admin_holds h WHERE h.profile_id=b.profile_id) AS held
                FROM reports r LEFT JOIN bindings b ON b.lodestone_id=r.lodestone_id
                WHERE r.day>$expired ORDER BY r.day, r.id LIMIT 51 OFFSET $offset;
                """,
            "plates" => """
                SELECT b.profile_id AS profile, b.name, b.world,
                    CAST(b.lodestone_id AS TEXT) AS characterId,
                    EXISTS(SELECT 1 FROM latest l WHERE l.profile_id=b.profile_id) AS published,
                    EXISTS(SELECT 1 FROM admin_holds h WHERE h.profile_id=b.profile_id) AS held
                FROM bindings b WHERE
                    ($filter='hidden' AND EXISTS(SELECT 1 FROM admin_holds h WHERE h.profile_id=b.profile_id)) OR
                    ($filter='' AND EXISTS(SELECT 1 FROM latest l WHERE l.profile_id=b.profile_id))
                ORDER BY b.profile_id LIMIT 51 OFFSET $offset;
                """,
            "audit" => """
                SELECT id, at, CAST(actor AS TEXT) AS actor, action, profile_id AS profile,
                    CAST(staff_id AS TEXT) AS staffId, reason FROM admin_audit
                WHERE at>$auditExpired ORDER BY id DESC LIMIT 51 OFFSET $offset;
                """,
            "staff" => "SELECT CAST(github_id AS TEXT) AS id, active FROM admin_staff ORDER BY github_id LIMIT 101;",
            _ => throw new ArgumentException("Unknown list."),
        };
        return await RowsAsync(connection, sql, cancellation, ("$offset", page * 50),
            ("$expired", clock.GetUtcNow().ToUnixTimeSeconds() / 86400 - 30),
            ("$auditExpired", clock.GetUtcNow().ToUnixTimeSeconds() - 30 * 86400), ("$filter", filter));
    }

    public async Task<Dictionary<string, object?>?> DetailAsync(string profile, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        var rows = await RowsAsync(connection, """
            SELECT b.profile_id AS profile, b.name, b.world, l.served, hex(l.marker) AS marker,
                EXISTS(SELECT 1 FROM admin_holds h WHERE h.profile_id=b.profile_id) AS held
            FROM bindings b LEFT JOIN latest l ON l.profile_id=b.profile_id WHERE b.profile_id=$profile;
            """, cancellation, ("$profile", profile));
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<StoredImage?> ImageAsync(string profile, string marker, int index, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        await using var command = Command(connection, null, """
            SELECT i.format,i.bytes FROM images i JOIN latest l ON l.persona=i.persona AND l.profile_id=i.profile_id
                JOIN bindings b ON b.profile_id=l.profile_id
            WHERE i.profile_id=$profile AND hex(l.marker)=$marker AND i.idx=$index;
            """, ("$profile", profile), ("$marker", marker), ("$index", index));
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        return await reader.ReadAsync(cancellation)
            ? new((AetherFrame.Protocol.Remote.ImageFormat)reader.GetInt32(0), (byte[])reader.GetValue(1)) : null;
    }

    /// <returns>HTTP status: 204 applied, 400 invalid, 403 revoked/forbidden, 409 changed since review.</returns>
    public async Task<int> ChangeAsync(StaffActor actor, DeskInput input, CancellationToken cancellation)
    {
        var reason = input.Reason.Trim();
        if (reason.Length is < 3 or > 500) return 400;
        await using var connection = await database.OpenAsync(cancellation);
        await using var transaction = connection.BeginTransaction();
        if (await FindStaffAsync(connection, transaction, actor.Id, cancellation) != actor) return 403;

        string? auditProfile = null;
        long? auditStaff = null;
        await using var command = Command(connection, transaction, "", ("$profile", input.Profile),
            ("$id", input.Id), ("$token", input.Token), ("$now", clock.GetUtcNow().ToUnixTimeSeconds()));
        if (input.Action is "hide" or "restore")
        {
            command.CommandText = """
                SELECT coalesce(hex(l.marker), '') || ':' ||
                    EXISTS(SELECT 1 FROM admin_holds h WHERE h.profile_id=b.profile_id)
                FROM bindings b LEFT JOIN latest l ON l.profile_id=b.profile_id WHERE b.profile_id=$profile;
                """;
            var expected = input.Marker + ":" + (input.Held == true ? "1" : "0");
            if (input.Held is null || !string.Equals(await command.ExecuteScalarAsync(cancellation) as string, expected, StringComparison.Ordinal)) return 409;
            if ((input.Action == "hide" && (input.Held == true || input.Marker.Length == 0)) ||
                (input.Action == "restore" && input.Held != true)) return 409;
            command.CommandText = input.Action == "hide"
                ? "INSERT INTO admin_holds(profile_id,created) VALUES($profile,$now);"
                : "DELETE FROM admin_holds WHERE profile_id=$profile;";
            auditProfile = input.Profile;
        }
        else if (input.Action == "dismiss")
        {
            command.CommandText = "DELETE FROM reports WHERE id=$id AND review_token=$token AND review_token<>'';";
        }
        else if (input.Action is "grant" or "revoke")
        {
            if (actor.Role != "owner") return 403;
            if (input.Id <= 0 || input.Id == options.OwnerGitHubId) return 400;
            command.CommandText = "SELECT COUNT(*) FROM admin_staff WHERE github_id<>$id;";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellation), CultureInfo.InvariantCulture) >= 100) return 409;
            command.CommandText = input.Action == "grant"
                ? "INSERT INTO admin_staff(github_id,active,generation) VALUES($id,1,1) ON CONFLICT(github_id) DO UPDATE SET active=1,generation=generation+1;"
                : "UPDATE admin_staff SET active=0,generation=generation+1 WHERE github_id=$id AND active=1;";
            auditStaff = input.Id;
        }
        else return 400;

        if (await command.ExecuteNonQueryAsync(cancellation) == 0) return 409;
        command.CommandText = "INSERT INTO admin_audit(at,actor,action,profile_id,staff_id,reason) VALUES($now,$actor,$action,$target,$staff,$reason);";
        command.Parameters.AddWithValue("$actor", actor.Id);
        command.Parameters.AddWithValue("$action", input.Action);
        command.Parameters.AddWithValue("$target", (object?)auditProfile ?? DBNull.Value);
        command.Parameters.AddWithValue("$staff", (object?)auditStaff ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", reason);
        await command.ExecuteNonQueryAsync(cancellation);
        await transaction.CommitAsync(cancellation);
        if (input.Action == "dismiss") await database.CheckpointAsync(CancellationToken.None);
        return 204;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static async Task<List<Dictionary<string, object?>>> RowsAsync(SqliteConnection connection, string sql, CancellationToken cancellation, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(connection, null, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(cancellation))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}
