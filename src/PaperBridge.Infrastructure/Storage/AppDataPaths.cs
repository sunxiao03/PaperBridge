namespace PaperBridge.Infrastructure.Storage;

public sealed class AppDataPaths
{
    public AppDataPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        RootDirectory = Path.GetFullPath(rootDirectory);
        DatabaseDirectory = Path.Combine(RootDirectory, "Data");
        DatabasePath = Path.Combine(DatabaseDirectory, "paperbridge.db");
        LibraryDirectory = Path.Combine(RootDirectory, "Library");
        BackupDirectory = Path.Combine(RootDirectory, "Backups");
        LogDirectory = Path.Combine(RootDirectory, "Logs");
        SettingsDirectory = Path.Combine(RootDirectory, "Settings");
        TranslationSettingsPath = Path.Combine(SettingsDirectory, "translation.json");
    }

    public string RootDirectory { get; }

    public string DatabaseDirectory { get; }

    public string DatabasePath { get; }

    public string LibraryDirectory { get; }

    public string BackupDirectory { get; }

    public string LogDirectory { get; }

    public string SettingsDirectory { get; }

    public string TranslationSettingsPath { get; }

    public static AppDataPaths CreateDefault()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("PAPERBRIDGE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return new AppDataPaths(overrideDirectory);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppDataPaths(Path.Combine(localAppData, "PaperBridge"));
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(LibraryDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(SettingsDirectory);
    }
}
