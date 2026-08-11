using PaperBridge.Application.Bilingual;

namespace PaperBridge.Application.Abstractions;

public interface IBilingualSegmentStore
{
    Task<IReadOnlyList<StoredBilingualSegment>> GetPageAsync(
        string documentHash,
        int pageIndex,
        CancellationToken cancellationToken = default);

    Task UpsertMachineTranslationAsync(
        StoredBilingualSegment segment,
        CancellationToken cancellationToken = default);

    Task SaveUserTranslationAsync(
        string documentHash,
        int pageIndex,
        string segmentId,
        string? userTranslation,
        CancellationToken cancellationToken = default);
}
