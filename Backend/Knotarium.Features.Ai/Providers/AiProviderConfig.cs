namespace Knotarium.Features.Ai.Providers;

/// <summary>Supported LLM vendor keys. The stored <see cref="AiProviderConfig.Vendor"/> is one of these.</summary>
public static class LlmVendors
{
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";
    public const string Azure = "azure";     // Azure OpenAI / Microsoft Copilot (OpenAI-compatible)
    public const string Gemini = "gemini";

    public static readonly string[] All = { Anthropic, OpenAi, Azure, Gemini };

    public static bool IsKnown(string? vendor) =>
        vendor is not null && System.Array.Exists(All, v => v == vendor);
}

/// <summary>
/// The active AI provider configuration, edited in the UI and persisted as a JSON blob in
/// <c>AppSetting["AiProviderConfig"]</c>. The API key itself never lives here — <see cref="CredentialRef"/>
/// points at an encrypted credential (or an <c>env:</c> ref) resolved through <c>ISecretResolver</c>.
/// </summary>
public sealed record AiProviderConfig(
    string Vendor,
    string Model,
    /// <summary>Reference to the API key: a credential id, or an <c>env:NAME</c> ref. Never the key itself.</summary>
    string CredentialRef,
    /// <summary>Override the vendor's default endpoint. Required for Azure (the resource URL).</summary>
    string? BaseUrl = null,
    /// <summary>Azure api-version, or an Anthropic anthropic-version override.</summary>
    string? ApiVersion = null,
    /// <summary>Optional per-config token cap; falls back to the operational default when null.</summary>
    int? MaxTokens = null)
{
    /// <summary>A config is usable only with a known vendor, a model, and a credential reference.</summary>
    public bool IsComplete =>
        LlmVendors.IsKnown(Vendor)
        && !string.IsNullOrWhiteSpace(Model)
        && !string.IsNullOrWhiteSpace(CredentialRef);
}
