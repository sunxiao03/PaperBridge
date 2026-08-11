using PaperBridge.Application.Annotations;
using PaperBridge.Domain.Documents;

namespace PaperBridge.Application.Abstractions;

public interface IAnnotationStore
{
    Task<IReadOnlyList<DocumentAnnotation>> GetForDocumentAsync(
        DocumentId documentId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(DocumentAnnotation annotation, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid annotationId, CancellationToken cancellationToken = default);
}
