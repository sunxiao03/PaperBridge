using Microsoft.Data.Sqlite;
using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Reading;
using PaperBridge.Infrastructure.Storage;

namespace PaperBridge.Infrastructure.Tests.Storage;

public sealed class SqliteReadingAssistantCacheTests
{
    [Fact]
    public async Task VersionSevenCacheRoundTripsAndTrimsOldestEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaperBridgeReadingCacheTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppDataPaths(root);
            await new ManagedDocumentLibrary(paths).InitializeAsync();
            var cache = new SqliteReadingAssistantCache(paths, maximumEntries: 1);
            var first = Key("first");
            var second = Key("second");

            await cache.SetAsync(first, Result("old", DateTimeOffset.UtcNow.AddMinutes(-1)));
            await cache.SetAsync(second, Result("new", DateTimeOffset.UtcNow));

            Assert.Null(await cache.GetAsync(first));
            Assert.Equal("new", (await cache.GetAsync(second))?.Content);
            await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(7L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('reading_assistant_cache');";
            Assert.Equal(12L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingVersionSixDatabaseMigratesToVersionSeven()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaperBridgeReadingCacheTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppDataPaths(root);
            var library = new ManagedDocumentLibrary(paths);
            await library.InitializeAsync();
            SqliteConnection.ClearAllPools();
            await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE reading_assistant_cache; PRAGMA user_version = 6;";
                await command.ExecuteNonQueryAsync();
            }

            await library.InitializeAsync();

            await using var migrated = new SqliteConnection($"Data Source={paths.DatabasePath}");
            await migrated.OpenAsync();
            await using var verify = migrated.CreateCommand();
            verify.CommandText = "PRAGMA user_version;";
            Assert.Equal(7L, (long)(await verify.ExecuteScalarAsync())!);
            verify.CommandText = "SELECT COUNT(*) FROM pragma_table_info('reading_assistant_cache');";
            Assert.Equal(12L, (long)(await verify.ExecuteScalarAsync())!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static ReadingAssistantCacheKey Key(string value) => new(
        new string('a', 64),
        ReadingTaskKind.SectionSummary,
        ReadingAssistantCacheKey.HashInput("system", value),
        "fake",
        "model",
        ReadingAssistantCoordinator.PromptVersion,
        "none");

    private static CachedReadingAssistantResult Result(string value, DateTimeOffset created) =>
        new(value, "model", 10, 5, created);
}
