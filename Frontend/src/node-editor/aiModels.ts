// Curated per-vendor model suggestions for the editable model combo. These are only *suggestions* — the
// field always accepts free text (custom / self-hosted / fine-tuned models), and the "load live" button in
// the AI provider settings can merge the vendor's actual model list on top. Keep this list short and current;
// adding a model is a one-line change (no backend redeploy needed since the field is free-text anyway).

export const CURATED_MODELS: Record<string, string[]> = {
  anthropic: [
    'claude-opus-4-8',
    'claude-sonnet-5',
    'claude-haiku-4-5-20251001',
    'claude-fable-5',
  ],
  openai: [
    'gpt-5.1',
    'gpt-5',
    'gpt-5-mini',
    'o4-mini',
    'gpt-4.1',
  ],
  // Azure deployment names are chosen by the operator, so there is nothing meaningful to curate.
  azure: [],
  gemini: [
    'gemini-2.5-pro',
    'gemini-2.5-flash',
    'gemini-2.0-flash',
  ],
};

/** Curated suggestions for a vendor (empty array for unknown vendors). */
export function curatedModelsFor(vendor: string | null | undefined): string[] {
  return (vendor && CURATED_MODELS[vendor]) || [];
}

/** Curated ∪ live, de-duplicated, curated first (stable order). */
export function mergeModelSuggestions(curated: string[], live: string[]): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const m of [...curated, ...live]) {
    const v = m.trim();
    if (v && !seen.has(v)) {
      seen.add(v);
      out.push(v);
    }
  }
  return out;
}
