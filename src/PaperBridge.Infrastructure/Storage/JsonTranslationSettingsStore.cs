using System.Text.Json;
using PaperBridge.Application.Abstractions;
using PaperBridge.Application.Translation;

namespace PaperBridge.Infrastructure.Storage;

public sealed class JsonTranslationSettingsStore : ITranslationSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly AppDataPaths _paths;

    public JsonTranslationSettingsStore(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    public async Task<TranslationServiceSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.TranslationSettingsPath))
        {
            return TranslationServiceSettings.Default;
        }

        await using var stream = new FileStream(
            _paths.TranslationSettingsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var settings = await JsonSerializer.DeserializeAsync<TranslationServiceSettings>(
            stream,
            SerializerOptions,
            cancellationToken);
        return (settings ?? TranslationServiceSettings.Default).Validate();
    }

    public async Task SaveAsync(
        TranslationServiceSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validated = settings.Validate();
        _paths.EnsureDirectoriesExist();
        var temporaryPath = Path.Combine(
            _paths.SettingsDirectory,
            $".translation-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, validated, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _paths.TranslationSettingsPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void TryDelete(string filePath)
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
