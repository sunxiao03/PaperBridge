using System.Globalization;
using Microsoft.Data.Sqlite;
using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Reading;

namespace PaperBridge.Infrastructure.Storage;

public sealed class SqliteReadingAssistantCache : IReadingAssistantCache
{
    private readonly string _connectionString;
    private readonly int _maximumEntries;

    public SqliteReadingAssistantCache(AppDataPaths paths, int maximumEntries = 5_000)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (maximumEntries is < 1 or > 20_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        _maximumEntries = maximumEntries;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true
        }.ToString();
    }

    public async Task<CachedReadingAssistantResult?> GetAsync(
        ReadingAssistantCacheKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT content, model, input_tokens, output_tokens, created_at_utc
            FROM reading_assistant_cache
            WHERE cache_key = $key;
            """;
        command.Parameters.AddWithValue("$key", key.ToStableId());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CachedReadingAssistantResult(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task SetAsync(
        ReadingAssistantCacheKey key,
        CachedReadingAssistantResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(result);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reading_assistant_cache (
                cache_key, document_hash, task_kind, input_hash, provider, model,
                prompt_version, custom_instruction_version, content, input_tokens,
                output_tokens, created_at_utc)
            VALUES (
                $key, $document, $task, $input, $provider, $model,
                $prompt, $custom, $content, $inputTokens, $outputTokens, $created)
            ON CONFLICT(cache_key) DO UPDATE SET
                content = excluded.content,
                input_tokens = excluded.input_tokens,
                output_tokens = excluded.output_tokens,
                created_at_utc = excluded.created_at_utc;
            """;
        command.Parameters.AddWithValue("$key", key.ToStableId());
        command.Parameters.AddWithValue("$document", key.DocumentHash);
        command.Parameters.AddWithValue("$task", (int)key.TaskKind);
        command.Parameters.AddWithValue("$input", key.InputHash);
        command.Parameters.AddWithValue("$provider", key.Provider);
        command.Parameters.AddWithValue("$model", key.Model);
        command.Parameters.AddWithValue("$prompt", key.PromptVersion);
        command.Parameters.AddWithValue("$custom", key.CustomInstructionVersion);
        command.Parameters.AddWithValue("$content", result.Content);
        command.Parameters.AddWithValue("$inputTokens", (object?)result.InputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$outputTokens", (object?)result.OutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", result.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var trim = connection.CreateCommand();
        trim.CommandText = """
            DELETE FROM reading_assistant_cache
            WHERE cache_key IN (
                SELECT cache_key FROM reading_assistant_cache
                ORDER BY created_at_utc DESC
                LIMIT -1 OFFSET $maximum
            );
            """;
        trim.Parameters.AddWithValue("$maximum", _maximumEntries);
        await trim.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
