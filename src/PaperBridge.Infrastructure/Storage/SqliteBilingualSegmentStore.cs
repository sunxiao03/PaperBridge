using System.Globalization;
using Microsoft.Data.Sqlite;
using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Bilingual;

namespace PaperBridge.Infrastructure.Storage;

public sealed class SqliteBilingualSegmentStore : IBilingualSegmentStore
{
    private readonly string _connectionString;

    public SqliteBilingualSegmentStore(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true
        }.ToString();
    }

    public async Task<IReadOnlyList<StoredBilingualSegment>> GetPageAsync(
        string documentHash,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        ValidateDocumentAndPage(documentHash, pageIndex);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_hash, page_index, segment_id, source_start, source_length,
                   source_text, source_text_hash, machine_translation, user_translation,
                   provider, model, prompt_version, glossary_version, layout_mode,
                   layout_confidence, degradation_reason, machine_updated_at_utc, user_updated_at_utc
            FROM bilingual_segments
            WHERE document_hash = $document AND page_index = $page
            ORDER BY source_start, segment_id;
            """;
        command.Parameters.AddWithValue("$document", documentHash);
        command.Parameters.AddWithValue("$page", pageIndex);
        var result = new List<StoredBilingualSegment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StoredBilingualSegment(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                (BilingualLayoutMode)reader.GetInt32(13),
                reader.GetDouble(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                ParseDate(reader.GetString(16)),
                reader.IsDBNull(17) ? null : ParseDate(reader.GetString(17))));
        }

        return result;
    }

    public async Task UpsertMachineTranslationAsync(
        StoredBilingualSegment segment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ValidateDocumentAndPage(segment.DocumentHash, segment.PageIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(segment.SegmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(segment.SourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(segment.MachineTranslation);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO bilingual_segments (
                document_hash, page_index, segment_id, source_start, source_length,
                source_text, source_text_hash, machine_translation, user_translation,
                provider, model, prompt_version, glossary_version, layout_mode,
                layout_confidence, degradation_reason, machine_updated_at_utc, user_updated_at_utc)
            VALUES (
                $document, $page, $segment, $start, $length,
                $source, $sourceHash, $machine, $user,
                $provider, $model, $prompt, $glossary, $layout,
                $confidence, $degradationReason, $machineUpdated, $userUpdated)
            ON CONFLICT(document_hash, page_index, segment_id) DO UPDATE SET
                source_start = excluded.source_start,
                source_length = excluded.source_length,
                source_text = excluded.source_text,
                source_text_hash = excluded.source_text_hash,
                machine_translation = excluded.machine_translation,
                provider = excluded.provider,
                model = excluded.model,
                prompt_version = excluded.prompt_version,
                glossary_version = excluded.glossary_version,
                layout_mode = excluded.layout_mode,
                layout_confidence = excluded.layout_confidence,
                degradation_reason = excluded.degradation_reason,
                machine_updated_at_utc = excluded.machine_updated_at_utc;
            """;
        AddParameters(command, segment);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveUserTranslationAsync(
        string documentHash,
        int pageIndex,
        string segmentId,
        string? userTranslation,
        CancellationToken cancellationToken = default)
    {
        ValidateDocumentAndPage(documentHash, pageIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        var normalized = string.IsNullOrWhiteSpace(userTranslation) ? null : userTranslation.Trim();
        if (normalized?.Length > 100_000)
        {
            throw new ArgumentException("User translation exceeds 100,000 characters.", nameof(userTranslation));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE bilingual_segments
            SET user_translation = $translation,
                user_updated_at_utc = $updated
            WHERE document_hash = $document AND page_index = $page AND segment_id = $segment;
            """;
        command.Parameters.AddWithValue("$translation", (object?)normalized ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", normalized is null ? DBNull.Value : DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$document", documentHash);
        command.Parameters.AddWithValue("$page", pageIndex);
        command.Parameters.AddWithValue("$segment", segmentId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("双语段落不存在。");
        }
    }

    private static void AddParameters(SqliteCommand command, StoredBilingualSegment segment)
    {
        command.Parameters.AddWithValue("$document", segment.DocumentHash);
        command.Parameters.AddWithValue("$page", segment.PageIndex);
        command.Parameters.AddWithValue("$segment", segment.SegmentId);
        command.Parameters.AddWithValue("$start", segment.SourceStart);
        command.Parameters.AddWithValue("$length", segment.SourceLength);
        command.Parameters.AddWithValue("$source", segment.SourceText);
        command.Parameters.AddWithValue("$sourceHash", segment.SourceTextHash);
        command.Parameters.AddWithValue("$machine", segment.MachineTranslation);
        command.Parameters.AddWithValue("$user", (object?)segment.UserTranslation ?? DBNull.Value);
        command.Parameters.AddWithValue("$provider", segment.Provider);
        command.Parameters.AddWithValue("$model", segment.Model);
        command.Parameters.AddWithValue("$prompt", segment.PromptVersion);
        command.Parameters.AddWithValue("$glossary", segment.GlossaryVersion);
        command.Parameters.AddWithValue("$layout", (int)segment.LayoutMode);
        command.Parameters.AddWithValue("$confidence", segment.LayoutConfidence);
        command.Parameters.AddWithValue("$degradationReason", (object?)segment.DegradationReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$machineUpdated", segment.MachineUpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$userUpdated", segment.UserUpdatedAtUtc is { } updated
            ? updated.ToString("O")
            : DBNull.Value);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void ValidateDocumentAndPage(string documentHash, int pageIndex)
    {
        if (string.IsNullOrWhiteSpace(documentHash) || documentHash.Length != 64)
        {
            throw new ArgumentException("Document hash must be a SHA-256 hexadecimal value.", nameof(documentHash));
        }

        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }
    }
}
