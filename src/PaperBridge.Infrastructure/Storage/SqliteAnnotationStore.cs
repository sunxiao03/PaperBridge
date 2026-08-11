using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Annotations;
using PaperBridge.Domain.Documents;

namespace PaperBridge.Infrastructure.Storage;

public sealed class SqliteAnnotationStore : IAnnotationStore
{
    public const int MaximumAnnotationsPerDocument = 10_000;
    private readonly string _connectionString;

    public SqliteAnnotationStore(AppDataPaths paths)
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

    public async Task<IReadOnlyList<DocumentAnnotation>> GetForDocumentAsync(
        DocumentId documentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, document_id, document_hash, page_index, kind, selected_text,
                   source_start, source_length, text_fingerprint, prefix_context,
                   suffix_context, rectangles_json, note_text, linked_translation,
                   color, anchor_status, created_at_utc, updated_at_utc
            FROM document_annotations
            WHERE document_id = $document
            ORDER BY page_index, source_start, created_at_utc
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$document", documentId.ToString());
        command.Parameters.AddWithValue("$maximum", MaximumAnnotationsPerDocument);
        var result = new List<DocumentAnnotation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DocumentAnnotation(
                Guid.Parse(reader.GetString(0)),
                new DocumentId(Guid.Parse(reader.GetString(1))),
                reader.GetString(2),
                reader.GetInt32(3),
                (AnnotationKind)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                DeserializeRectangles(reader.GetString(11)),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetString(14),
                (AnnotationAnchorStatus)reader.GetInt32(15),
                ParseDate(reader.GetString(16)),
                ParseDate(reader.GetString(17))));
        }

        return result;
    }

    public async Task SaveAsync(DocumentAnnotation annotation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var limit = connection.CreateCommand())
        {
            limit.Transaction = (SqliteTransaction)transaction;
            limit.CommandText = """
                SELECT COUNT(*), COUNT(CASE WHEN id = $id THEN 1 END)
                FROM document_annotations
                WHERE document_id = $document;
                """;
            limit.Parameters.AddWithValue("$id", annotation.Id.ToString("D"));
            limit.Parameters.AddWithValue("$document", annotation.DocumentId.ToString());
            await using var reader = await limit.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt32(0) >= MaximumAnnotationsPerDocument && reader.GetInt32(1) == 0)
            {
                throw new InvalidOperationException($"单篇文档最多保存 {MaximumAnnotationsPerDocument} 条标记。");
            }
        }

        if (annotation.Kind == AnnotationKind.Bookmark)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = """
                DELETE FROM document_annotations
                WHERE document_id = $document AND page_index = $page AND kind = 3 AND id <> $id;
                """;
            delete.Parameters.AddWithValue("$document", annotation.DocumentId.ToString());
            delete.Parameters.AddWithValue("$page", annotation.PageIndex);
            delete.Parameters.AddWithValue("$id", annotation.Id.ToString("D"));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO document_annotations (
                id, document_id, document_hash, page_index, kind, selected_text,
                source_start, source_length, text_fingerprint, prefix_context,
                suffix_context, rectangles_json, note_text, linked_translation,
                color, anchor_status, created_at_utc, updated_at_utc)
            VALUES (
                $id, $document, $hash, $page, $kind, $selected,
                $start, $length, $fingerprint, $prefix,
                $suffix, $rectangles, $note, $translation,
                $color, $status, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                document_hash = excluded.document_hash,
                page_index = excluded.page_index,
                kind = excluded.kind,
                selected_text = excluded.selected_text,
                source_start = excluded.source_start,
                source_length = excluded.source_length,
                text_fingerprint = excluded.text_fingerprint,
                prefix_context = excluded.prefix_context,
                suffix_context = excluded.suffix_context,
                rectangles_json = excluded.rectangles_json,
                note_text = excluded.note_text,
                linked_translation = excluded.linked_translation,
                color = excluded.color,
                anchor_status = excluded.anchor_status,
                updated_at_utc = excluded.updated_at_utc;
            """;
        AddParameters(command, annotation);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid annotationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM document_annotations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", annotationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(SqliteCommand command, DocumentAnnotation annotation)
    {
        command.Parameters.AddWithValue("$id", annotation.Id.ToString("D"));
        command.Parameters.AddWithValue("$document", annotation.DocumentId.ToString());
        command.Parameters.AddWithValue("$hash", annotation.DocumentHash);
        command.Parameters.AddWithValue("$page", annotation.PageIndex);
        command.Parameters.AddWithValue("$kind", (int)annotation.Kind);
        command.Parameters.AddWithValue("$selected", (object?)annotation.SelectedText ?? DBNull.Value);
        command.Parameters.AddWithValue("$start", annotation.SourceStart);
        command.Parameters.AddWithValue("$length", annotation.SourceLength);
        command.Parameters.AddWithValue("$fingerprint", (object?)annotation.TextFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$prefix", (object?)annotation.PrefixContext ?? DBNull.Value);
        command.Parameters.AddWithValue("$suffix", (object?)annotation.SuffixContext ?? DBNull.Value);
        command.Parameters.AddWithValue("$rectangles", JsonSerializer.Serialize(annotation.Rectangles));
        command.Parameters.AddWithValue("$note", (object?)annotation.NoteText ?? DBNull.Value);
        command.Parameters.AddWithValue("$translation", (object?)annotation.LinkedTranslation ?? DBNull.Value);
        command.Parameters.AddWithValue("$color", annotation.Color);
        command.Parameters.AddWithValue("$status", (int)annotation.AnchorStatus);
        command.Parameters.AddWithValue("$created", annotation.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", annotation.UpdatedAtUtc.ToString("O"));
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

    private static IReadOnlyList<NormalizedPdfRectangle> DeserializeRectangles(string json) =>
        JsonSerializer.Deserialize<NormalizedPdfRectangle[]>(json) ?? [];

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
