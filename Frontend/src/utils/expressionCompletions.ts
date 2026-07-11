import { variableRefExpression } from './variableExpression';

/** One `{{ }}` autocomplete candidate: what to show and what to insert. */
export interface ExpressionCompletion {
  /** Short label shown in the list (the variable/field name). */
  label: string;
  /** Secondary hint (type · source). */
  detail: string;
  /** The full expression inserted in place of the open `{{…` fragment. */
  insertText: string;
}

/** Minimal variable shape needed to build a completion (a subset of the store's VariableRecord). */
export interface CompletionVariable {
  name: string;
  type?: string;
  producer?: string;
  producerOutput?: string;
  derived?: boolean;
}

/**
 * Build `{{ }}` expression completions from the workflow's known variables — promoted upstream node
 * outputs (referenced via `$node.<id>.output.<field>`) and Set Variable globals (`$variables.<name>`).
 * These are exactly the references an author reaches for; sourcing from the variable store keeps the
 * completion perf-safe (no live-graph subscription) and consistent with drag-to-reference.
 *
 * `query` is the text already typed after `{{`; it filters candidates case-insensitively by name or by
 * the expression text. Duplicate insert expressions are collapsed.
 */
export function buildExpressionCompletions(variables: CompletionVariable[], query = ''): ExpressionCompletion[] {
  const normalizedQuery = query.trim().toLowerCase();
  const seen = new Set<string>();
  const completions: ExpressionCompletion[] = [];

  for (const variable of variables) {
    if (!variable.name) {
      continue;
    }
    const insertText = variableRefExpression(variable);
    if (seen.has(insertText)) {
      continue;
    }
    seen.add(insertText);

    if (normalizedQuery
      && !variable.name.toLowerCase().includes(normalizedQuery)
      && !insertText.toLowerCase().includes(normalizedQuery)) {
      continue;
    }

    const source = variable.derived
      ? 'variable'
      : variable.producer
        ? `from ${variable.producer}`
        : 'variable';
    completions.push({
      label: variable.name,
      detail: [variable.type, source].filter(Boolean).join(' · '),
      insertText,
    });
  }

  return completions;
}

/**
 * Given the raw text of an expression field and the caret offset, detect an *open* `{{` fragment (one
 * not yet closed by `}}` before the caret) and return the fragment's start offset + the query text typed
 * after it. Returns null when the caret is not inside an open `{{ … }}`. Pure — drives the popover.
 */
export function findOpenExpression(text: string, caret: number): { start: number; query: string } | null {
  const before = text.slice(0, caret);
  const open = before.lastIndexOf('{{');
  if (open === -1) {
    return null;
  }
  // If a closing }} appears between the last {{ and the caret, the fragment is already closed.
  if (before.indexOf('}}', open + 2) !== -1) {
    return null;
  }
  return { start: open, query: before.slice(open + 2) };
}
