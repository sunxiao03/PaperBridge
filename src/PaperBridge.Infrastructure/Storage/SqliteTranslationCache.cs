using System.Globalization;
using Microsoft.Data.Sqlite;
using PaperBridge.Application.Abstractions;
using PaperBridge.Domain.Translations;

namespace PaperBridge.Infrastructure.Storage;

public sealed class SqliteTranslationCache : ITranslationCache
{
    private readonly string _connectionString;
    private readonly int _maximumEntries;

    public SqliteTranslationCache(AppDataPaths paths, int maximumEntries = 20_000)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (maximumEntries is < 1 or > 100_000)
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

    public async Task<CachedTranslation?> GetAsync(
        TranslationCacheKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        const string sql = """
            SELECT translation, model, input_tokens, output_tokens, created_at_utc
            FROM translation_cache
            WHERE cache_key = $key;
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$key", key.ToStableId());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CachedTranslation(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task SetAsync(
        TranslationCacheKey key,
        CachedTranslation translation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(translation);
        const string sql = """
            INSERT INTO translation_cache (
                cache_key, document_hash, source_text_hash, provider, model, granularity,
                source_language, target_language, prompt_version, custom_instruction_version,
                glossary_version, translation, input_tokens, output_tokens, created_at_utc)
            VALUES (
                $key, $document, $source, $provider, $model, $granularity,
                $sourceLanguage, $targetLanguage, $prompt, $custom, $glossary,
                $translation, $inputTokens, $outputTokens, $created)
            ON CONFLICT(cache_key) DO UPDATE SET
                translation = excluded.translation,
                input_tokens = excluded.input_tokens,
                output_tokens = excluded.output_tokens,
                created_at_utc = excluded.created_at_utc;
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$key", key.ToStableId());
        command.Parameters.AddWithValue("$document", key.DocumentHash);
        command.Parameters.AddWithValue("$source", key.SourceTextHash);
        command.Parameters.AddWithValue("$provider", key.Provider);
        command.Parameters.AddWithValue("$model", key.Model);
        command.Parameters.AddWithValue("$granularity", key.Granularity.ToString());
        command.Parameters.AddWithValue("$sourceLanguage", key.SourceLanguage);
        command.Parameters.AddWithValue("$targetLanguage", key.TargetLanguage);
        command.Parameters.AddWithValue("$prompt", key.PromptVersion);
        command.Parameters.AddWithValue("$custom", key.CustomInstructionVersion);
        command.Parameters.AddWithValue("$glossary", key.GlossaryVersion);
        command.Parameters.AddWithValue("$translation", translation.Translation);
        command.Parameters.AddWithValue("$inputTokens", (object?)translation.InputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$outputTokens", (object?)translation.OutputTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", translation.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var trimCommand = connection.CreateCommand();
        trimCommand.CommandText = """
            DELETE FROM translation_cache
            WHERE cache_key IN (
                SELECT cache_key
                FROM translation_cache
                ORDER BY created_at_utc DESC
                LIMIT -1 OFFSET $maximumEntries
            );
            """;
        trimCommand.Parameters.AddWithValue("$maximumEntries", _maximumEntries);
        await trimCommand.ExecuteNonQueryAsync(cancellationToken);
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
