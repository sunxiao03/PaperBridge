using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PaperBridge.Infrastructure.Storage;

/// <summary>
/// Creates consistent SQLite snapshots and restores them while the application is not using the database.
/// </summary>
public sealed class SqliteDatabaseBackupService
{
    public const int DefaultAutomaticBackupRetention = 5;
    private const string AutomaticBackupPrefix = "paperbridge-auto-";
    private readonly AppDataPaths _paths;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public SqliteDatabaseBackupService(AppDataPaths paths, Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Creates at most one automatic snapshot per UTC calendar day and retains the newest snapshots.
    /// </summary>
    public async Task<string?> CreateDailyBackupIfDueAsync(
        int retentionCount = DefaultAutomaticBackupRetention,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionCount, 1);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            _paths.EnsureDirectoriesExist();
            var now = _utcNow().ToUniversalTime();
            var newest = EnumerateAutomaticBackups()
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is not null && File.GetLastWriteTimeUtc(newest).Date >= now.UtcDateTime.Date)
            {
                return null;
            }

            var destination = Path.Combine(
                _paths.BackupDirectory,
                $"{AutomaticBackupPrefix}{now:yyyyMMdd-HHmmssfff}Z.db");
            await CreateSnapshotCoreAsync(destination, cancellationToken);
            File.SetLastWriteTimeUtc(destination, now.UtcDateTime);
            PruneAutomaticBackups(retentionCount);
            return destination;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Creates a consistent database snapshot at an explicitly selected path.
    /// </summary>
    public async Task<string> CreateBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var fullDestination = Path.GetFullPath(destinationPath);
            await CreateSnapshotCoreAsync(fullDestination, cancellationToken);
            return fullDestination;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Restores a verified snapshot. All PaperBridge database users must be closed before calling this method.
    /// A verified rollback snapshot is created first and returned to the caller.
    /// </summary>
    public async Task<string?> RestoreBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var source = Path.GetFullPath(backupPath);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("The database backup was not found.", source);
            }

            await VerifyIntegrityAsync(source, cancellationToken);
            _paths.EnsureDirectoriesExist();

            string? rollbackPath = null;
            if (File.Exists(_paths.DatabasePath))
            {
                var now = _utcNow().ToUniversalTime();
                rollbackPath = Path.Combine(
                    _paths.BackupDirectory,
                    $"paperbridge-before-restore-{now:yyyyMMdd-HHmmssfff}Z.db");
                await CreateSnapshotCoreAsync(rollbackPath, cancellationToken);
            }

            var temporary = Path.Combine(
                _paths.DatabaseDirectory,
                $".restore-{Guid.NewGuid():N}.tmp");
            try
            {
                await CopySnapshotAsync(source, temporary, cancellationToken);
                await VerifyIntegrityAsync(temporary, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                SqliteConnection.ClearAllPools();
                if (File.Exists(_paths.DatabasePath))
                {
                    File.Replace(temporary, _paths.DatabasePath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, _paths.DatabasePath);
                }

                DeleteDatabaseSidecarIfPresent(_paths.DatabasePath + "-wal");
                DeleteDatabaseSidecarIfPresent(_paths.DatabasePath + "-shm");
            }
            finally
            {
                TryDeleteFile(temporary);
            }

            return rollbackPath;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public static async Task VerifyIntegrityAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SQLite integrity check failed: {result ?? "no result"}.");
        }
    }

    private async Task CreateSnapshotCoreAsync(string destinationPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.DatabasePath))
        {
            throw new FileNotFoundException("The PaperBridge database was not found.", _paths.DatabasePath);
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("The backup destination must include a directory.", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);
        if (File.Exists(destinationPath))
        {
            throw new IOException($"The backup destination already exists: '{destinationPath}'.");
        }

        var temporary = Path.Combine(destinationDirectory, $".backup-{Guid.NewGuid():N}.tmp");
        try
        {
            await CopySnapshotAsync(_paths.DatabasePath, temporary, cancellationToken);
            await VerifyIntegrityAsync(temporary, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destinationPath);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static async Task CopySnapshotAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(sourcePath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(destinationPath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        await using var source = new SqliteConnection(sourceConnectionString);
        await using var destination = new SqliteConnection(destinationConnectionString);
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private IEnumerable<string> EnumerateAutomaticBackups() =>
        Directory.Exists(_paths.BackupDirectory)
            ? Directory.EnumerateFiles(_paths.BackupDirectory, $"{AutomaticBackupPrefix}*.db")
            : [];

    private void PruneAutomaticBackups(int retentionCount)
    {
        foreach (var oldBackup in EnumerateAutomaticBackups()
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(retentionCount))
        {
            File.Delete(oldBackup);
        }
    }

    private static void DeleteDatabaseSidecarIfPresent(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
