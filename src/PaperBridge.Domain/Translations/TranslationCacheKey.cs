using System.Security.Cryptography;
using System.Text;

namespace PaperBridge.Domain.Translations;

public sealed record TranslationCacheKey
{
    public TranslationCacheKey(
        string documentHash,
        string sourceText,
        string provider,
        string model,
        string promptVersion,
        string glossaryVersion,
        TranslationGranularity granularity = TranslationGranularity.Selection,
        string customInstructionVersion = "none",
        string sourceLanguage = "en",
        string targetLanguage = "zh-CN")
    {
        DocumentHash = Required(documentHash);
        SourceTextHash = Hash(Required(sourceText));
        Provider = Required(provider).ToLowerInvariant();
        Model = Required(model);
        PromptVersion = Required(promptVersion);
        GlossaryVersion = Required(glossaryVersion);
        Granularity = granularity;
        CustomInstructionVersion = Required(customInstructionVersion);
        SourceLanguage = Required(sourceLanguage);
        TargetLanguage = Required(targetLanguage);
    }

    public string DocumentHash { get; }

    public string SourceTextHash { get; }

    public string Provider { get; }

    public string Model { get; }

    public string PromptVersion { get; }

    public string GlossaryVersion { get; }

    public TranslationGranularity Granularity { get; }

    public string CustomInstructionVersion { get; }

    public string SourceLanguage { get; }

    public string TargetLanguage { get; }

    public string ToStableId()
    {
        var canonical = string.Join(
            '\n',
            DocumentHash,
            SourceTextHash,
            Provider,
            Model,
            PromptVersion,
            GlossaryVersion,
            Granularity.ToString(),
            CustomInstructionVersion,
            SourceLanguage,
            TargetLanguage);

        return Hash(canonical);
    }

    private static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
