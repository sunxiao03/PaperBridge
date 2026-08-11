namespace PaperBridge.Domain.Documents;

public sealed record LibraryFolder(Guid Id, string Name, DateTimeOffset CreatedAtUtc);
