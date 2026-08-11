using Microsoft.Data.Sqlite;
using PaperBridge.Domain.Glossaries;
using PaperBridge.Infrastructure.Storage;

namespace PaperBridge.Infrastructure.Tests.Storage;

public sealed class SqliteGlossaryStoreTests
{
    [Fact]
    public async Task Initialize_SeedsPersonalAndPendingBuiltInGlossariesAndMigratesSchema()
    {
        var root = CreateRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var store = new SqliteGlossaryStore(paths);

            await store.InitializeAsync();
            var snapshot = await store.GetSnapshotAsync();

            Assert.Contains(snapshot.Glossaries, glossary =>
                glossary.Id == SqliteGlossaryStore.PersonalGlossaryId && glossary.Source == GlossarySource.User);
            var builtInTerms = snapshot.Terms.Where(term => term.GlossaryId == SqliteGlossaryStore.ReactorPhysicsGlossaryId).ToArray();
            Assert.Equal(24, builtInTerms.Length);
            Assert.All(builtInTerms, term => Assert.Equal(GlossaryReviewStatus.Pending, term.ReviewStatus));

            await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(7L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task PersonalTerm_RoundTripsUpdatesByNormalizedEnglishAndDeletes()
    {
        var root = CreateRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var store = new SqliteGlossaryStore(paths);
            await store.InitializeAsync();
            var first = new GlossaryTerm(
                " Neutron   Flux ",
                "旧译名",
                GlossarySource.User,
                glossaryId: SqliteGlossaryStore.PersonalGlossaryId,
                englishAliases: ["flux"],
                notes: "first");
            await store.SaveTermAsync(first);
            await store.SaveTermAsync(new GlossaryTerm(
                "neutron flux",
                "中子通量",
                GlossarySource.User,
                priority: 5,
                glossaryId: SqliteGlossaryStore.PersonalGlossaryId,
                reviewStatus: GlossaryReviewStatus.Approved));

            var snapshot = await store.GetSnapshotAsync();
            var saved = Assert.Single(snapshot.Terms, term =>
                term.GlossaryId == SqliteGlossaryStore.PersonalGlossaryId && term.English == "neutron flux");
            Assert.Equal("中子通量", saved.PreferredChinese);
            Assert.Equal(5, saved.Priority);

            await store.DeleteTermAsync(saved.Id);
            snapshot = await store.GetSnapshotAsync();
            Assert.DoesNotContain(snapshot.Terms, term => term.Id == saved.Id);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task ReviewAndEnableChangesPersistAcrossStoreInstances()
    {
        var root = CreateRoot();
        try
        {
            var paths = new AppDataPaths(root);
            var store = new SqliteGlossaryStore(paths);
            await store.InitializeAsync();
            var snapshot = await store.GetSnapshotAsync();
            var pending = snapshot.Terms.First(term => term.GlossaryId == SqliteGlossaryStore.ReactorPhysicsGlossaryId);
            var approved = new GlossaryTerm(
                pending.English, pending.PreferredChinese, pending.Source, pending.Priority, pending.Category,
                pending.Explanation, pending.SourceReference, pending.Id, pending.GlossaryId,
                pending.EnglishAliases, pending.ChineseAliases, pending.Notes,
                GlossaryReviewStatus.Approved, DateTimeOffset.UtcNow);
            await store.SaveTermAsync(approved);
            await store.SetGlossaryEnabledAsync(SqliteGlossaryStore.ReactorPhysicsGlossaryId, false);

            var reloaded = await new SqliteGlossaryStore(paths).GetSnapshotAsync();

            Assert.Equal(GlossaryReviewStatus.Approved, reloaded.Terms.Single(term => term.Id == pending.Id).ReviewStatus);
            Assert.False(reloaded.Glossaries.Single(glossary => glossary.Id == SqliteGlossaryStore.ReactorPhysicsGlossaryId).IsEnabled);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaperBridgeGlossaryTests", Guid.NewGuid().ToString("N"));
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
