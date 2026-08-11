namespace PaperBridge.Application.Abstractions;

public interface ICredentialStore
{
    Task SaveAsync(string account, string secret, CancellationToken cancellationToken = default);

    Task<string?> GetAsync(string account, CancellationToken cancellationToken = default);

    Task DeleteAsync(string account, CancellationToken cancellationToken = default);
}

