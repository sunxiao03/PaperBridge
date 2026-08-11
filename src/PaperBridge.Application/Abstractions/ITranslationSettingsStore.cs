using PaperBridge.Application.Translation;

namespace PaperBridge.Application.Abstractions;

public interface ITranslationSettingsStore
{
    Task<TranslationServiceSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        TranslationServiceSettings settings,
        CancellationToken cancellationToken = default);
}
