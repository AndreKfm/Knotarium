/** Minimal view of a variable record needed to build its read expression. */
export interface VariableRefLike {
  name: string;
  producer?: string;
  producerOutput?: string;
  /** True for Set Variable-declared globals (vs. promoted node outputs). */
  derived?: boolean;
}

/**
 * Expand a global-store pill into the `{{ ... }}` expression that reads it.
 *
 * - A Set Variable-declared global (`derived`) lives in the variable bag and is NOT
 *   exposed as a node output, so it must read via `$variables.<name>`. (Its producer
 *   node only emits `result`.)
 * - A promoted node-output variable reads from that node's output port, so it uses the
 *   `$node.<id>.output.<field>` form — which also tolerates hyphenated node ids that the
 *   `$variables.` tokenizer would choke on.
 */
export function variableRefExpression(ref: VariableRefLike): string {
  if (!ref.derived && ref.producer && ref.producerOutput) {
    return `{{ $node.${ref.producer}.output.${ref.producerOutput} }}`;
  }
  return `{{ $variables.${ref.name} }}`;
}
