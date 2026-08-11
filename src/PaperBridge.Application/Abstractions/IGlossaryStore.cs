using PaperBridge.Domain.Glossaries;

namespace PaperBridge.Application.Abstractions;

public interface IGlossaryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<GlossarySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<GlossaryDefinition> CreatePersonalGlossaryAsync(
        string name,
        string? topic = null,
        CancellationToken cancellationToken = default);

    Task SetGlossaryEnabledAsync(Guid glossaryId, bool enabled, CancellationToken cancellationToken = default);

    Task SaveTermAsync(GlossaryTerm term, CancellationToken cancellationToken = default);

    Task DeleteTermAsync(Guid termId, CancellationToken cancellationToken = default);
}

public sealed record GlossarySnapshot(
    IReadOnlyList<GlossaryDefinition> Glossaries,
    IReadOnlyList<GlossaryTerm> Terms)
{
    public GlossaryDefinition PersonalGlossary => Glossaries.First(glossary => glossary.Source == GlossarySource.User);
}
