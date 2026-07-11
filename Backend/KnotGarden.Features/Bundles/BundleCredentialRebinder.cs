using System.Collections.Generic;
using KnotGarden.Features.Portability;

using KnotGarden.Features.Execution;

namespace KnotGarden.Features.Bundles;

// ─────────────────────────────────────────────────────────────────────────────
// Credential-slot rebinding for bundles — now a thin adapter over the shared
// CredentialSlotModule (the recursive walk + both portability directions live
// there, so bundles and templates can never drift). This type preserves the
// bundle-facing API (Rebind / CredentialRebindResult / SlotPrefix).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The rewritten document plus which slots were resolved and which were left as placeholders.</summary>
public sealed record CredentialRebindResult(
    WorkflowExportDocument Document,
    IReadOnlyList<string> ReboundSlots,
    IReadOnlyList<string> UnboundSlots);

/// <summary>Pure rewrite of <c>slot:&lt;Slot&gt;</c> credential placeholders to real credential ids.</summary>
public static class BundleCredentialRebinder
{
    /// <summary>The placeholder prefix a bundled workflow uses to reference a credential slot.</summary>
    public const string SlotPrefix = CredentialSlotTokens.SlotPrefix;

    /// <summary>Rewrites every <c>slot:&lt;Slot&gt;</c> placeholder to the bound credential id.</summary>
    public static CredentialRebindResult Rebind(
        WorkflowExportDocument document,
        IReadOnlyDictionary<string, string> slotBindings)
    {
        var result = CredentialSlotModule.RebindSlotsToIds(document, slotBindings);
        return new CredentialRebindResult(result.Document, result.ReboundSlots, result.UnboundSlots);
    }
}
