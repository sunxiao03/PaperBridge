using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Bilingual;
using PaperBridge.Domain.Translations;
using PaperBridge.Infrastructure.Storage;

namespace PaperBridge.Infrastructure.Tests.Storage;

public sealed class SqliteBilingualSegmentStoreTests
{
    [Fact]
    public async Task MachineRetranslationPreservesUserEditUntilExplicitlyCleared()
    {
        var root = CreateRoot();
        try
        {
            var paths = new AppDataPaths(root);
            await new ManagedDocumentLibrary(paths).InitializeAsync();
            var store = new SqliteBilingualSegmentStore(paths);
            var first = CreateSegment("机器译文 1");
            await store.UpsertMachineTranslationAsync(first);
            await store.SaveUserTranslationAsync(first.DocumentHash, 0, first.SegmentId, "我的译文");

            await store.UpsertMachineTranslationAsync(first with
            {
                MachineTranslation = "机器译文 2",
                MachineUpdatedAtUtc = first.MachineUpdatedAtUtc.AddMinutes(1)
            });
            var saved = Assert.Single(await store.GetPageAsync(first.DocumentHash, 0));

            Assert.Equal("机器译文 2", saved.MachineTranslation);
            Assert.Equal("我的译文", saved.DisplayTranslation);
            Assert.True(saved.HasUserTranslation);

            await store.SaveUserTranslationAsync(first.DocumentHash, 0, first.SegmentId, null);
            saved = Assert.Single(await store.GetPageAsync(first.DocumentHash, 0));
            Assert.Equal("机器译文 2", saved.DisplayTranslation);
            Assert.False(saved.HasUserTranslation);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task VersionFiveMigrationCreatesBoundedBilingualSegmentSchema()
    {
        var root = CreateRoot();
        try
        {
            var paths = new AppDataPaths(root);
            await new ManagedDocumentLibrary(paths).InitializeAsync();
            await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('bilingual_segments');";
            Assert.Equal(18L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(7L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task TranslationCacheEnforcesConfiguredEntryLimit()
    {
        var root = CreateRoot();
        try
        {
            var paths = new AppDataPaths(root);
            await new ManagedDocumentLibrary(paths).InitializeAsync();
            var cache = new SqliteTranslationCache(paths, maximumEntries: 2);
            var keys = Enumerable.Range(0, 3).Select(index => new TranslationCacheKey(
                new string('a', 64),
                $"source {index}",
                "openai",
                "model",
                "prompt",
                "glossary",
                TranslationGranularity.Paragraph,
                "custom")).ToArray();
            for (var index = 0; index < keys.Length; index++)
            {
                await cache.SetAsync(keys[index], new CachedTranslation(
                    $"translation {index}",
                    "model",
                    null,
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(index)));
            }

            Assert.Null(await cache.GetAsync(keys[0]));
            Assert.NotNull(await cache.GetAsync(keys[1]));
            Assert.NotNull(await cache.GetAsync(keys[2]));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static StoredBilingualSegment CreateSegment(string translation)
    {
        const string source = "The reactor is critical.";
        return new StoredBilingualSegment(
            new string('a', 64),
            0,
            "p00001-0001",
            0,
            source.Length,
            source,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
            translation,
            null,
            "openai",
            "model",
            "prompt-v1",
            "glossary-v1",
            BilingualLayoutMode.Paragraph,
            0.9,
            null,
            DateTimeOffset.UtcNow,
            null);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaperBridgeBilingualTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
