/**
 * Format a global-store variable's value for display in a pill.
 *
 * A value is only meaningful once a run resolves it. Until then — or if the resolved
 * value is `undefined` (e.g. a keyed-write head container that hasn't run yet) — show
 * "Awaiting run" rather than the literal string "undefined".
 */
export function formatVariableValue(
  status: 'awaiting run' | 'resolved' | undefined,
  value: unknown,
): string {
  if (status !== 'resolved' || value === undefined) return 'Awaiting run';
  if (value === null) return 'null';
  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}

/** True when the (resolved) value is an array — drives the [] vs {} container glyph. */
export function isArrayValue(value: unknown): boolean {
  return Array.isArray(value);
}

/**
 * Human-readable kind for tooltips: arrays read "array", other objects read "dictionary",
 * scalars keep their primitive name. `containerKind` (inferred from a keyed path) wins;
 * otherwise a resolved array value is detected.
 */
export function variableKindLabel(
  type: 'string' | 'number' | 'boolean' | 'object',
  containerKind: 'object' | 'array' | undefined,
  value: unknown,
): string {
  if (containerKind === 'array' || (type === 'object' && isArrayValue(value))) return 'array';
  if (type === 'object') return 'dictionary';
  return type;
}

/**
 * Suffix-notation type sigil shown after a variable name (no glyph, no word):
 * {} dictionary, [] array, "" string, # number, ? boolean. `containerKind`
 * (inferred from a keyed path) wins; otherwise a resolved array value is detected.
 */
export function variableTypeSuffix(
  type: 'string' | 'number' | 'boolean' | 'object',
  containerKind: 'object' | 'array' | undefined,
  value: unknown,
): string {
  if (containerKind === 'array' || (type === 'object' && isArrayValue(value))) return '[]';
  switch (type) {
    case 'object': return '{}';
    case 'string': return '""';
    case 'number': return '#';
    case 'boolean': return '?';
  }
}
