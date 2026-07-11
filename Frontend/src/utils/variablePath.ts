/**
 * Mirrors the backend VariablePath grammar (head + .name / ["key"] / [index] segments)
 * just enough for the editor to know the *container* variable a Set Variable write targets.
 *
 * The head is the run of characters up to the first '.' or '[' — the global that actually
 * gets created/updated. For `myDict["name"]` the head is `myDict`; for a plain `counter`
 * it's `counter`.
 */
export function variablePathHead(reference: string): string {
  const ref = reference.trim();
  let i = 0;
  while (i < ref.length && ref[i] !== '.' && ref[i] !== '[') i++;
  return ref.slice(0, i);
}

/** True when the reference navigates into a nested member/index (i.e. has a path beyond the head). */
export function hasVariablePath(reference: string): boolean {
  const ref = reference.trim();
  return variablePathHead(ref).length !== ref.length;
}

/**
 * The kind of the *head container*, inferred purely from the first path segment's syntax
 * (no run needed): a string key (`["name"]` / `.name`) means the head is an object/dictionary;
 * an integer index (`[0]`) means it's an array. Returns undefined for a bare name (no path).
 */
export function pathContainerKind(reference: string): 'object' | 'array' | undefined {
  const ref = reference.trim();
  const i = variablePathHead(ref).length;
  if (i >= ref.length) return undefined; // bare name, no path
  if (ref[i] === '.') return 'object';
  // ref[i] === '[': a quoted body is a string key (object); a bare body is an integer index (array).
  const next = ref[i + 1];
  return next === '"' || next === "'" ? 'object' : 'array';
}
