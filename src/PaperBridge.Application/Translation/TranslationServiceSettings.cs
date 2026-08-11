namespace PaperBridge.Application.Translation;

public sealed record TranslationServiceSettings(
    string ProviderId,
    string BaseUrl,
    string Model,
    string? CustomInstruction,
    int RequestTimeoutSeconds = 60,
    int MaxConcurrency = 2)
{
    public const string OpenAiProviderId = "openai";
    public const string DeepSeekProviderId = "deepseek";
    public const string CompatibleProviderId = "openai-compatible";

    public static TranslationServiceSettings Default => new(
        OpenAiProviderId,
        "https://api.openai.com/v1/",
        "gpt-4.1-mini",
        CustomInstruction: null);

    public TranslationServiceSettings Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(BaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Base URL 必须是有效的 HTTPS 地址。", nameof(BaseUrl));
        }

        if (RequestTimeoutSeconds is < 5 or > 600)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeoutSeconds));
        }

        if (MaxConcurrency is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency));
        }

        return this with
        {
            ProviderId = ProviderId.Trim().ToLowerInvariant(),
            BaseUrl = BaseUrl.Trim().TrimEnd('/') + "/",
            Model = Model.Trim(),
            CustomInstruction = string.IsNullOrWhiteSpace(CustomInstruction) ? null : CustomInstruction.Trim()
        };
    }
}
