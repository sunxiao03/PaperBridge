using Microsoft.Data.Sqlite;
using PaperBridge.Infrastructure.Storage;

namespace PaperBridge.Infrastructure.Tests.Storage;

public sealed class SqliteDatabaseBackupServiceTests
{
    [Fact]
    public async Task BackupCapturesCommittedWalDataAndRestoreReturnsIt()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = new AppDataPaths(root);
            paths.EnsureDirectoriesExist();
            await CreateDatabaseAsync(paths.DatabasePath, "before");
            var service = new SqliteDatabaseBackupService(paths);
            var backup = await service.CreateBackupAsync(Path.Combine(paths.BackupDirectory, "manual.db"));

            await SetValueAsync(paths.DatabasePath, "after");
            var rollback = await service.RestoreBackupAsync(backup);

            Assert.Equal("before", await ReadValueAsync(paths.DatabasePath));
            Assert.NotNull(rollback);
            Assert.Equal("after", await ReadValueAsync(rollback!));
            await SqliteDatabaseBackupService.VerifyIntegrityAsync(paths.DatabasePath);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task CorruptBackupIsRejectedWithoutChangingCurrentDatabase()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = new AppDataPaths(root);
            paths.EnsureDirectoriesExist();
            await CreateDatabaseAsync(paths.DatabasePath, "current");
            var corrupt = Path.Combine(paths.BackupDirectory, "corrupt.db");
            await File.WriteAllBytesAsync(corrupt, [0x50, 0x42, 0x00, 0x01]);
            var service = new SqliteDatabaseBackupService(paths);

            await Assert.ThrowsAnyAsync<SqliteException>(() => service.RestoreBackupAsync(corrupt));

            Assert.Equal("current", await ReadValueAsync(paths.DatabasePath));
            Assert.Empty(Directory.EnumerateFiles(paths.BackupDirectory, "paperbridge-before-restore-*.db"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task AutomaticBackupRunsOncePerDayAndRetainsNewestFive()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = new AppDataPaths(root);
            paths.EnsureDirectoriesExist();
            await CreateDatabaseAsync(paths.DatabasePath, "day-0");
            var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            var service = new SqliteDatabaseBackupService(paths, () => now);

            Assert.NotNull(await service.CreateDailyBackupIfDueAsync());
            Assert.Null(await service.CreateDailyBackupIfDueAsync());
            for (var day = 1; day <= 6; day++)
            {
                now = now.AddDays(1);
                await SetValueAsync(paths.DatabasePath, $"day-{day}");
                var created = await service.CreateDailyBackupIfDueAsync();
                Assert.NotNull(created);
                File.SetLastWriteTimeUtc(created!, now.UtcDateTime);
            }

            var backups = Directory.EnumerateFiles(paths.BackupDirectory, "paperbridge-auto-*.db").ToArray();
            Assert.Equal(5, backups.Length);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static async Task CreateDatabaseAsync(string path, string value)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE state(value TEXT NOT NULL); INSERT INTO state VALUES ($value); PRAGMA user_version=7;";
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetValueAsync(string path, string value)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE state SET value=$value;";
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadValueAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM state;";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"paperbridge-backup-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
