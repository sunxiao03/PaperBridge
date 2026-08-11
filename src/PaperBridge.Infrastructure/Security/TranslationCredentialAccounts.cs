namespace PaperBridge.Infrastructure.Security;

public static class TranslationCredentialAccounts
{
    public static string ForProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return $"translation/{providerId.Trim().ToLowerInvariant()}";
    }
}
