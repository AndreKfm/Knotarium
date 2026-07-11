namespace KnotGarden.Features.Ai;

/// <summary>
/// Operational (non-vendor) knobs for AI workflow generation, bound from the <c>Ai</c> config section.
/// The <em>vendor / model / API-key credential</em> are NOT here — they are edited in the UI and persisted
/// as <see cref="Providers.AiProviderConfig"/> (see <see cref="IAiProviderConfigStore"/>). This holds only
/// the loop/limit defaults.
/// </summary>
public sealed class AiGenerationOptions
{
    public const string SectionName = "Ai";

    /// <summary>Default token cap for a completion (a provider config may override it per vendor).</summary>
    public int MaxTokens { get; set; } = 8000;

    /// <summary>Upper bound on generate→compile→repair passes.</summary>
    public int MaxRepairAttempts { get; set; } = 3;

    /// <summary>Hard cap on accepted intent length, to bound prompt size and abuse.</summary>
    public int MaxIntentLength { get; set; } = 4000;
}
