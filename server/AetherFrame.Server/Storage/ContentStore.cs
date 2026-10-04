using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AetherFrame.Protocol.Identity;
using AetherFrame.Protocol.Remote;
using Microsoft.Data.Sqlite;

namespace AetherFrame.Server.Storage;

/// <summary>What publishing a revision did (decisions N2 and C4).</summary>
internal enum PublishResult
{
    /// <summary>The revision is now the character's latest.</summary>
    Published,

    /// <summary>The revision was already accepted with these exact bytes: nothing changed.</summary>
    AlreadyKnown,

    /// <summary>The revision id was already accepted with other bytes.</summary>
    Conflict,

    /// <summary>The binding changed while the revision was being checked: nothing was stored.</summary>
    NotBound,
}

/// <summary>One served image: its format and its re-encoded bytes.</summary>
internal sealed record StoredImage(ImageFormat Format, byte[] Bytes);

/// <summary>
/// Each binding's published content (decisions N2, D6, C4 and C7): its latest revision's document,
/// served profile, marker and images, and a record of every revision id it accepted with the
/// document's SHA-256, kept until the binding goes. Publishing a revision prunes the previous one at
/// once. Reports live here too, for 30 days or until the operator acts.
/// </summary>
internal sealed class ContentStore(ServerDatabase database, TimeProvider time)
{
    /// <summary>How long a report is kept when the operator hasn't acted on it (decision C5).</summary>
    public static readonly TimeSpan ReportLifetime = TimeSpan.FromDays(30);

    private long Today => time.GetUtcNow().ToUnixTimeSeconds() / 86_400;

    /// <summary>
    /// Whether a revision id is new, known with the same bytes, or known with other bytes (rule 4),
    /// before any image is processed.
    /// </summary>
    public async Task<PublishResult?> CheckRevisionAsync(PersonaId persona, ProfileId profileId, RevisionId revisionId, byte[] documentSha256, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        var known = await KnownDigestAsync(connection, null, persona, profileId, revisionId, cancellation);
        return known is null ? null : CryptographicOperations.FixedTimeEquals(known, documentSha256) ? PublishResult.AlreadyKnown : PublishResult.Conflict;
    }

    /// <summary>
    /// Makes a verified revision the binding's latest, in one transaction: records (revision id,
    /// SHA-256), replaces the latest document, served profile, marker and images, and so prunes the
    /// previous revision's. The binding must still hold <paramref name="profileId"/>, and the revision
    /// id must still be new.
    /// </summary>
    public async Task<PublishResult> PublishAsync(PersonaId persona, ProfileId profileId, RevisionId revisionId, byte[] document, byte[] documentSha256, byte[] served, RevisionMarker marker, IReadOnlyList<StoredImage> images, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellation);

        await using (var bound = connection.CreateCommand())
        {
            bound.Transaction = transaction;
            bound.CommandText = "SELECT 1 FROM bindings WHERE persona = $persona AND profile_id = $profile;";
            bound.Parameters.AddWithValue("$persona", persona.ToString());
            bound.Parameters.AddWithValue("$profile", profileId.ToString());
            if (await bound.ExecuteScalarAsync(cancellation) is null)
            {
                return PublishResult.NotBound;
            }
        }

        var known = await KnownDigestAsync(connection, transaction, persona, profileId, revisionId, cancellation);
        if (known is not null)
        {
            return CryptographicOperations.FixedTimeEquals(known, documentSha256) ? PublishResult.AlreadyKnown : PublishResult.Conflict;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO revisions (persona, profile_id, revision_id, sha256) VALUES ($persona, $profile, $revision, $sha);
                DELETE FROM images WHERE persona = $persona AND profile_id = $profile;
                INSERT INTO latest (persona, profile_id, sequence, revision_id, document, served, marker)
                VALUES ($persona, $profile, (SELECT COALESCE(MAX(sequence), 0) + 1 FROM latest WHERE persona = $persona AND profile_id = $profile), $revision, $document, $served, $marker)
                ON CONFLICT (persona, profile_id) DO UPDATE SET sequence = latest.sequence + 1, revision_id = excluded.revision_id, document = excluded.document, served = excluded.served, marker = excluded.marker;
                """;
            command.Parameters.AddWithValue("$persona", persona.ToString());
            command.Parameters.AddWithValue("$profile", profileId.ToString());
            command.Parameters.AddWithValue("$revision", revisionId.ToArray());
            command.Parameters.AddWithValue("$sha", documentSha256);
            command.Parameters.AddWithValue("$document", document);
            command.Parameters.AddWithValue("$served", served);
            command.Parameters.AddWithValue("$marker", marker.ToArray());
            await command.ExecuteNonQueryAsync(cancellation);
        }

        for (var index = 0; index < images.Count; index++)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO images (persona, profile_id, idx, format, bytes) VALUES ($persona, $profile, $index, $format, $bytes);";
            insert.Parameters.AddWithValue("$persona", persona.ToString());
            insert.Parameters.AddWithValue("$profile", profileId.ToString());
            insert.Parameters.AddWithValue("$index", index);
            insert.Parameters.AddWithValue("$format", (int)images[index].Format);
            insert.Parameters.AddWithValue("$bytes", images[index].Bytes);
            await insert.ExecuteNonQueryAsync(cancellation);
        }

        await transaction.CommitAsync(cancellation);
        await database.CheckpointAsync(CancellationToken.None);
        return PublishResult.Published;
    }

    /// <summary>The latest served profile of a binding, or null when it has none.</summary>
    public async Task<byte[]?> ServedAsync(PersonaId persona, ProfileId profileId, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT served FROM latest WHERE persona = $persona AND profile_id = $profile AND NOT EXISTS(SELECT 1 FROM admin_holds WHERE profile_id=$profile);";
        command.Parameters.AddWithValue("$persona", persona.ToString());
        command.Parameters.AddWithValue("$profile", profileId.ToString());
        return await command.ExecuteScalarAsync(cancellation) as byte[];
    }

    /// <summary>
    /// Image <paramref name="index"/> of a binding's latest revision, only when
    /// <paramref name="marker"/> is that revision's (decision D6): an older marker gets nothing, so a
    /// refresh never mixes two revisions.
    /// </summary>
    public async Task<StoredImage?> ImageAsync(PersonaId persona, ProfileId profileId, RevisionMarker marker, int index, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT latest.marker, images.format, images.bytes FROM latest JOIN images ON images.persona = latest.persona AND images.profile_id = latest.profile_id WHERE latest.persona = $persona AND latest.profile_id = $profile AND images.idx = $index AND NOT EXISTS(SELECT 1 FROM admin_holds WHERE profile_id=$profile);";
        command.Parameters.AddWithValue("$persona", persona.ToString());
        command.Parameters.AddWithValue("$profile", profileId.ToString());
        command.Parameters.AddWithValue("$index", index);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        if (!await reader.ReadAsync(cancellation))
        {
            return null;
        }

        var current = (byte[])reader.GetValue(0);
        return CryptographicOperations.FixedTimeEquals(current, marker.ToArray())
            ? new StoredImage((ImageFormat)reader.GetInt32(1), (byte[])reader.GetValue(2))
            : null;
    }

    /// <summary>
    /// Pausing (decision C3): deletes the binding's latest revision and its images at once, and keeps
    /// the binding and its revision records, so sharing resumes with the next publish and an old
    /// revision can't return. True when anything was published.
    /// </summary>
    public async Task<bool> PauseAsync(PersonaId persona, CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM images WHERE persona = $persona; DELETE FROM latest WHERE persona = $persona;";
        command.Parameters.AddWithValue("$persona", persona.ToString());
        var deleted = await command.ExecuteNonQueryAsync(cancellation) > 0;
        if (deleted)
        {
            await database.CheckpointAsync(CancellationToken.None);
        }

        return deleted;
    }

    /// <summary>
    /// Drops reports past 30 days (decision C5). <see cref="Housekeeping"/> runs it every hour, so
    /// none outlives its time for want of a new one.
    /// </summary>
    public async Task<int> DropExpiredReportsAsync(CancellationToken cancellation)
    {
        await using var connection = await database.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reports WHERE day <= $expired; DELETE FROM admin_audit WHERE at <= $auditExpired;";
        command.Parameters.AddWithValue("$expired", Today - (long)ReportLifetime.TotalDays);
        command.Parameters.AddWithValue("$auditExpired", time.GetUtcNow().ToUnixTimeSeconds() - 30 * 86400);
        var dropped = await command.ExecuteNonQueryAsync(cancellation);
        if (dropped > 0)
        {
            await database.CheckpointAsync(CancellationToken.None);
        }

        return dropped;
    }

    /// <summary>Keeps a report for the operator (decision C5): the reported character, a reason and the reporting key, with the day.</summary>
    public async Task ReportAsync(long lodestoneId, string reason, PersonaId reporter, CancellationToken cancellation)
    {
        await DropExpiredReportsAsync(cancellation);
        await using var connection = await database.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO reports (lodestone_id, reason, reporter, day, review_token) VALUES ($id, $reason, $reporter, $today, $token);";
        command.Parameters.AddWithValue("$token", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$id", lodestoneId);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$reporter", reporter.ToString());
        command.Parameters.AddWithValue("$today", Today);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    private static async Task<byte[]?> KnownDigestAsync(SqliteConnection connection, SqliteTransaction? transaction, PersonaId persona, ProfileId profileId, RevisionId revisionId, CancellationToken cancellation)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sha256 FROM revisions WHERE persona = $persona AND profile_id = $profile AND revision_id = $revision;";
        command.Parameters.AddWithValue("$persona", persona.ToString());
        command.Parameters.AddWithValue("$profile", profileId.ToString());
        command.Parameters.AddWithValue("$revision", revisionId.ToArray());
        return await command.ExecuteScalarAsync(cancellation) as byte[];
    }
}

/// <summary>Hourly housekeeping: drops reports past their 30 days (decision C5), whether or not any new report arrives.</summary>
internal sealed class Housekeeping(ContentStore content) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await content.DropExpiredReportsAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The next hour tries again.
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
