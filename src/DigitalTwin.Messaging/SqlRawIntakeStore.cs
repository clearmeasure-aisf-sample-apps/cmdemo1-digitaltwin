using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DigitalTwin.Core.Intake;
using DigitalTwin.Core.Observation;
using DigitalTwin.Core.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DigitalTwin.Messaging;

/// <summary>
/// Raw intake table (system of record) in the twin SQL database. Written with plain ADO.NET so the insert
/// can share one transaction with the Wolverine outbox.
/// </summary>
public sealed class SqlRawIntakeStore(IOptions<MessagingOptions> options) : IRawIntakeStore
{
    private const string Columns =
        "sequence, id, source, kind, channel, received_at, dedupe_key, headers_json, body_gzip, processed_at, applied_count, attempts, last_error, dead_lettered_at";

    private const string SchemaSql = """
        IF SCHEMA_ID('twin') IS NULL EXEC('CREATE SCHEMA twin');
        IF OBJECT_ID('twin.raw_intake_events') IS NULL
        BEGIN
            CREATE TABLE twin.raw_intake_events (
                sequence bigint IDENTITY(1,1) NOT NULL,
                id uniqueidentifier NOT NULL,
                source nvarchar(32) NOT NULL,
                kind nvarchar(32) NOT NULL,
                channel nvarchar(16) NOT NULL,
                received_at datetimeoffset NOT NULL,
                dedupe_key nvarchar(256) NULL,
                headers_json nvarchar(max) NOT NULL,
                body_gzip varbinary(max) NOT NULL,
                body_bytes int NOT NULL,
                processed_at datetimeoffset NULL,
                applied_count int NULL,
                attempts int NOT NULL CONSTRAINT df_raw_intake_attempts DEFAULT 0,
                last_error nvarchar(2000) NULL,
                dead_lettered_at datetimeoffset NULL,
                CONSTRAINT pk_raw_intake_events PRIMARY KEY NONCLUSTERED (id)
            );
            CREATE UNIQUE CLUSTERED INDEX ix_raw_intake_sequence ON twin.raw_intake_events (sequence);
            CREATE INDEX ix_raw_intake_pending ON twin.raw_intake_events (processed_at, dead_lettered_at, received_at);
        END
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_raw_intake_received' AND object_id = OBJECT_ID('twin.raw_intake_events'))
            CREATE INDEX ix_raw_intake_received ON twin.raw_intake_events (received_at) INCLUDE (processed_at);
        """;

    // Retention reads and deletes whole days; the Basic tier needs more than the 30 s default.
    private const int RetentionCommandTimeoutSeconds = 120;

    private string ConnectionString => options.Value.ConnectionString;

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(SchemaSql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<int> InsertAsync(SqlConnection conn, SqlTransaction tx, RawIntakeEvent raw, CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(raw.Body);
        await using var cmd = new SqlCommand("""
            INSERT INTO twin.raw_intake_events (id, source, kind, channel, received_at, dedupe_key, headers_json, body_gzip, body_bytes)
            VALUES (@id, @source, @kind, @channel, @received_at, @dedupe_key, @headers_json, @body_gzip, @body_bytes);
            """, conn, tx);
        cmd.Parameters.AddWithValue("@id", raw.Id);
        cmd.Parameters.AddWithValue("@source", raw.Source.ToString());
        cmd.Parameters.AddWithValue("@kind", raw.Kind.ToString());
        cmd.Parameters.AddWithValue("@channel", raw.Channel.ToString());
        cmd.Parameters.AddWithValue("@received_at", raw.ReceivedAt);
        cmd.Parameters.AddWithValue("@dedupe_key", (object?)raw.DedupeKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@headers_json", JsonSerializer.Serialize(raw.Headers));
        cmd.Parameters.AddWithValue("@body_gzip", Compress(bodyBytes));
        cmd.Parameters.AddWithValue("@body_bytes", bodyBytes.Length);
        await cmd.ExecuteNonQueryAsync(ct);
        return bodyBytes.Length;
    }

    public async Task<RawIntakeEvent?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand($"SELECT {Columns} FROM twin.raw_intake_events WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<RawIntakeEvent>> ReadPageAsync(long afterSequence, int take, CancellationToken ct)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            $"SELECT TOP (@take) {Columns} FROM twin.raw_intake_events WHERE sequence > @after ORDER BY sequence", conn);
        cmd.Parameters.AddWithValue("@take", take);
        cmd.Parameters.AddWithValue("@after", afterSequence);
        var list = new List<RawIntakeEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public async Task MarkProcessedAsync(Guid id, int appliedCount, DateTimeOffset processedAt, CancellationToken ct)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "UPDATE twin.raw_intake_events SET processed_at = @at, applied_count = @applied WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@at", processedAt);
        cmd.Parameters.AddWithValue("@applied", appliedCount);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordFailureAsync(Guid id, string failure, bool deadLettered, DateTimeOffset failedAt, CancellationToken ct)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            UPDATE twin.raw_intake_events
            SET attempts = attempts + 1,
                last_error = @error,
                dead_lettered_at = CASE WHEN @dead = 1 THEN @at ELSE dead_lettered_at END
            WHERE id = @id
            """, conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@error", failure.Length > 2000 ? failure[..2000] : failure);
        cmd.Parameters.AddWithValue("@dead", deadLettered);
        cmd.Parameters.AddWithValue("@at", failedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<RawIntakeStats> GetStatsAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT
                COUNT_BIG(*),
                COUNT_BIG(processed_at),
                SUM(CASE WHEN processed_at IS NULL AND dead_lettered_at IS NULL THEN 1 ELSE 0 END),
                MIN(CASE WHEN processed_at IS NULL AND dead_lettered_at IS NULL THEN received_at END),
                SUM(CASE WHEN processed_at IS NULL AND dead_lettered_at IS NOT NULL THEN 1 ELSE 0 END),
                MAX(received_at),
                MAX(processed_at)
            FROM twin.raw_intake_events
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new RawIntakeStats(
            Total: reader.GetInt64(0),
            Processed: reader.GetInt64(1),
            Pending: reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            OldestPendingReceivedAt: reader.IsDBNull(3) ? null : reader.GetDateTimeOffset(3),
            DeadLettered: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            LastCapturedAt: reader.IsDBNull(5) ? null : reader.GetDateTimeOffset(5),
            LastProcessedAt: reader.IsDBNull(6) ? null : reader.GetDateTimeOffset(6));
    }

    /// <summary>Walks the days with index seeks on received_at: one probe per day instead of a scan of the raw table.</summary>
    public async Task<IReadOnlyList<DateOnly>> GetArchivableDaysAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var days = new List<DateOnly>();
        var from = DateTimeOffset.MinValue;
        while (true)
        {
            await using var cmd = new SqlCommand("""
                SELECT MIN(received_at) FROM twin.raw_intake_events
                WHERE received_at >= @from AND received_at < @before AND processed_at IS NOT NULL
                """, conn) { CommandTimeout = RetentionCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@before", before);
            if (await cmd.ExecuteScalarAsync(ct) is not DateTimeOffset first)
            {
                return days;
            }
            var day = DateOnly.FromDateTime(first.UtcDateTime);
            var dayEnd = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            if (dayEnd > before)
            {
                return days;
            }
            days.Add(day);
            from = dayEnd;
        }
    }

    public async Task<IReadOnlyList<RawIntakeEvent>> ReadProcessedDayPageAsync(DateOnly day, long afterSequence, int take, CancellationToken ct)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        // The sequence bound comes from the received_at index; the page itself is a clustered seek on sequence.
        await using var bounds = new SqlCommand(
            "SELECT MAX(sequence) FROM twin.raw_intake_events WHERE received_at >= @start AND received_at < @end", conn)
            { CommandTimeout = RetentionCommandTimeoutSeconds };
        bounds.Parameters.AddWithValue("@start", start);
        bounds.Parameters.AddWithValue("@end", start.AddDays(1));
        if (await bounds.ExecuteScalarAsync(ct) is not long last)
        {
            return [];
        }

        await using var cmd = new SqlCommand($"""
            SELECT TOP (@take) {Columns} FROM twin.raw_intake_events
            WHERE sequence > @after AND sequence <= @last
              AND processed_at IS NOT NULL AND received_at >= @start AND received_at < @end
            ORDER BY sequence
            """, conn) { CommandTimeout = RetentionCommandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@take", take);
        cmd.Parameters.AddWithValue("@after", afterSequence);
        cmd.Parameters.AddWithValue("@last", last);
        cmd.Parameters.AddWithValue("@start", start);
        cmd.Parameters.AddWithValue("@end", start.AddDays(1));
        var list = new List<RawIntakeEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public async Task<double> GetSizeMbAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT CAST(ISNULL(SUM(a.used_pages), 0) * 8 / 1024.0 AS float)
            FROM sys.partitions p JOIN sys.allocation_units a ON a.container_id = p.partition_id
            WHERE p.object_id = OBJECT_ID('twin.raw_intake_events')
            """, conn);
        return Math.Round((double)(await cmd.ExecuteScalarAsync(ct) ?? 0d), 1);
    }

    public async Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var deleted = 0;
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        foreach (var chunk in ids.Chunk(500))
        {
            await using var cmd = new SqlCommand(
                "DELETE FROM twin.raw_intake_events WHERE id IN (SELECT CAST(value AS uniqueidentifier) FROM OPENJSON(@ids))", conn)
                { CommandTimeout = RetentionCommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(chunk));
            deleted += await cmd.ExecuteNonQueryAsync(ct);
        }
        return deleted;
    }

    private static RawIntakeEvent Read(SqlDataReader r)
        => new()
        {
            Sequence = r.GetInt64(0),
            Id = r.GetGuid(1),
            Source = Enum.Parse<EventSourceKind>(r.GetString(2)),
            Kind = Enum.Parse<IntakeKind>(r.GetString(3)),
            Channel = Enum.Parse<IngestionChannel>(r.GetString(4)),
            ReceivedAt = r.GetDateTimeOffset(5),
            DedupeKey = r.IsDBNull(6) ? null : r.GetString(6),
            Headers = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(7)) ?? [],
            Body = Encoding.UTF8.GetString(Decompress((byte[])r[8])),
            ProcessedAt = r.IsDBNull(9) ? null : r.GetDateTimeOffset(9),
            AppliedCount = r.IsDBNull(10) ? null : r.GetInt32(10),
            Attempts = r.GetInt32(11),
            LastError = r.IsDBNull(12) ? null : r.GetString(12),
            DeadLetteredAt = r.IsDBNull(13) ? null : r.GetDateTimeOffset(13)
        };

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(data);
        }
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
