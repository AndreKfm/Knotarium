// Extract-to-subflow: the pure, framework-free core behind the editor's "Extract selection into a
// subflow" action. Given a node/edge graph and a set of selected node ids, it (1) validates that the
// selection can be replaced by a single subflow CALL node without changing behavior, (2) works out the
// variable I/O that must cross the new boundary, and (3) plans the rewrite (child subflow content + call
// node + the parent edges to redirect).
//
// Behavior-preservation rests on three facts about Knotarium subflows:
//   * A subflow has ONE `start` and ONE `end`; the call node has a single `in`/`result`. So the selection
//     must be a single-entry/single-exit (SESE) region — one node receiving external control, one sending
//     it out (either may be absent for a head/tail region).
//   * Data crosses via VARIABLES, not ports, and the compiler namespaces the first token after
//     `$variables.` (so `$variables.signal.params.x` -> `$variables.sf_..__signal.params.x`). Any
//     variable ROOT read inside but not written inside must be passed IN (this is how the ambient
//     `signal` run-context survives); any root written inside and read outside must be passed OUT.
//   * Node-output references (`$node.<id>`) are tied to node identity. If the selection straddles such a
//     reference (a kept node reads a moved node's output, or vice versa) the variable seam can't carry it,
//     so we refuse rather than silently break.

export interface ExNode {
  id: string;
  type: string;
  properties: Record<string, unknown>;
  /** Trigger-only nodes (event/schedule/etc.) cannot live inside a subflow — it is invoked, not triggered. */
  triggerOnly?: boolean;
}

export interface ExEdge {
  id: string;
  source: string;
  sourceHandle: string | null;
  target: string;
  targetHandle: string | null;
}

export interface VarRef {
  name: string;
  /** 'string' until type inference is added; the subflow interface tolerates any. */
  type: 'string';
}

export interface ExtractAnalysis {
  ok: boolean;
  /** Present (and the only field that matters) when ok === false. */
  reason?: string;
  /** The single node external control enters, or null when the region is a flow head (no external predecessor). */
  entryNodeId: string | null;
  /** The single node external control leaves from, or null when the region is terminal. */
  exitNodeId: string | null;
  entryEdges: ExEdge[];
  exitEdges: ExEdge[];
  internalEdges: ExEdge[];
  /** Variable roots read inside but not written inside — passed into the subflow (incl. ambient `signal`). */
  inputs: string[];
  /** Variable roots written inside and read by some kept node — passed back out. */
  outputs: string[];
}

const VARIABLES_TOKEN = '$variables.';
// Both spellings appear in the codebase; treat either as a node-output reference.
const NODE_TOKENS = ['$node.', '$nodes.'];

function isIdentifierChar(c: string): boolean {
  return /[A-Za-z0-9_]/.test(c);
}

/** First dotted segment of a variable path: `signal.params.x` -> `signal`, matching how the compiler scopes. */
export function rootOf(name: string): string {
  const dot = name.indexOf('.');
  return dot === -1 ? name : name.slice(0, dot);
}

/** Pull every `$variables.<root>` and `$node[s].<id>` reference out of one string value. */
function scanString(s: string, varRoots: Set<string>, nodeRefs: Set<string>): void {
  // $variables.<root> — read identifier chars after the token; the root is the first dotted segment.
  let i = 0;
  while ((i = s.indexOf(VARIABLES_TOKEN, i)) !== -1) {
    let end = i + VARIABLES_TOKEN.length;
    while (end < s.length && (isIdentifierChar(s[end]) || s[end] === '.')) end++;
    const path = s.slice(i + VARIABLES_TOKEN.length, end);
    if (path) varRoots.add(rootOf(path));
    i = end;
  }
  for (const token of NODE_TOKENS) {
    let j = 0;
    while ((j = s.indexOf(token, j)) !== -1) {
      let end = j + token.length;
      while (end < s.length && isIdentifierChar(s[end])) end++;
      const id = s.slice(j + token.length, end);
      if (id) nodeRefs.add(id);
      j = end;
    }
  }
}

/** Recursively scan a value (object/array/string) for variable + node references. */
function scanValue(v: unknown, varRoots: Set<string>, nodeRefs: Set<string>): void {
  if (typeof v === 'string') {
    scanString(v, varRoots, nodeRefs);
  } else if (Array.isArray(v)) {
    for (const item of v) scanValue(item, varRoots, nodeRefs);
  } else if (v && typeof v === 'object') {
    for (const val of Object.values(v as Record<string, unknown>)) scanValue(val, varRoots, nodeRefs);
  }
}

/** Variable references + node-output references a node makes through its properties. */
export function refsOf(node: ExNode): { varRoots: Set<string>; nodeRefs: Set<string> } {
  const varRoots = new Set<string>();
  const nodeRefs = new Set<string>();
  scanValue(node.properties, varRoots, nodeRefs);
  return { varRoots, nodeRefs };
}

/** Variable roots a node WRITES (the only writers are setVariable / setVariables). */
export function writesOf(node: ExNode): Set<string> {
  const out = new Set<string>();
  if (node.type === 'setVariable') {
    const name = node.properties?.variableName;
    if (typeof name === 'string' && name) out.add(rootOf(name));
  } else if (node.type === 'setVariables') {
    const vars = node.properties?.variables;
    if (Array.isArray(vars)) {
      for (const entry of vars) {
        const key = (entry as { key?: unknown })?.key;
        if (typeof key === 'string' && key) out.add(rootOf(key));
      }
    } else if (vars && typeof vars === 'object') {
      for (const key of Object.keys(vars as Record<string, unknown>)) out.add(rootOf(key));
    }
  }
  return out;
}

/**
 * Validate a selection and compute the boundary + variable I/O for a single-region extraction.
 * Returns `ok: false` with a human reason when the selection cannot be cleanly extracted.
 */
export function analyzeExtraction(nodes: ExNode[], edges: ExEdge[], selectedIds: string[]): ExtractAnalysis {
  const empty: ExtractAnalysis = {
    ok: false, entryNodeId: null, exitNodeId: null,
    entryEdges: [], exitEdges: [], internalEdges: [], inputs: [], outputs: [],
  };

  const sel = new Set(selectedIds);
  const selNodes = nodes.filter((n) => sel.has(n.id));
  if (selNodes.length === 0) return { ...empty, reason: 'Select at least one node to extract.' };

  const trigger = selNodes.find((n) => n.triggerOnly);
  if (trigger) {
    return { ...empty, reason: 'A trigger node can’t move into a subflow — leave the trigger in place and select the nodes after it.' };
  }

  const entryEdges = edges.filter((e) => sel.has(e.target) && !sel.has(e.source));
  const exitEdges = edges.filter((e) => sel.has(e.source) && !sel.has(e.target));
  const internalEdges = edges.filter((e) => sel.has(e.source) && sel.has(e.target));

  // Single entry: every external edge entering the selection must land on the same node, and that node is
  // the entry. With no external entry, the region must have exactly one internal root (a node with no
  // internal predecessor) to stand in as the entry.
  const entryTargets = new Set(entryEdges.map((e) => e.target));
  let entryNodeId: string | null;
  if (entryTargets.size > 1) {
    return { ...empty, reason: 'The selection has more than one entry point. Select a region that external steps enter at a single node.' };
  } else if (entryTargets.size === 1) {
    entryNodeId = [...entryTargets][0];
  } else {
    const internalTargetIds = new Set(internalEdges.map((e) => e.target));
    const roots = selNodes.filter((n) => !internalTargetIds.has(n.id));
    if (roots.length !== 1) {
      return { ...empty, reason: 'The selection has no single starting node. Select a connected chain with one entry.' };
    }
    entryNodeId = roots[0].id;
  }

  // Single exit: symmetric. Multiple distinct source nodes leaving the selection can't be one call node.
  const exitSources = new Set(exitEdges.map((e) => e.source));
  let exitNodeId: string | null;
  if (exitSources.size > 1) {
    return { ...empty, reason: 'The selection has more than one exit point. Select a region that leaves to external steps from a single node.' };
  } else if (exitSources.size === 1) {
    exitNodeId = [...exitSources][0];
  } else {
    exitNodeId = null; // terminal region — the call node's `result` is left unconnected, as the tail was.
  }

  // Connectivity: the selection must be one connected region (undirected over internal edges), else a
  // single call node can't represent it.
  if (selNodes.length > 1) {
    const adj = new Map<string, string[]>();
    for (const n of selNodes) adj.set(n.id, []);
    for (const e of internalEdges) {
      adj.get(e.source)!.push(e.target);
      adj.get(e.target)!.push(e.source);
    }
    const seen = new Set<string>([entryNodeId]);
    const stack = [entryNodeId];
    while (stack.length) {
      const cur = stack.pop()!;
      for (const nb of adj.get(cur) ?? []) if (!seen.has(nb)) { seen.add(nb); stack.push(nb); }
    }
    if (seen.size !== selNodes.length) {
      return { ...empty, reason: 'The selected nodes aren’t all connected. Select one connected region (Stage 3 will handle several at once).' };
    }
  }

  // Node-output references must not straddle the boundary in either direction.
  const selIdSet = sel;
  for (const n of selNodes) {
    const { nodeRefs } = refsOf(n);
    for (const ref of nodeRefs) {
      if (!selIdSet.has(ref) && nodes.some((m) => m.id === ref)) {
        return { ...empty, reason: `A selected node reads another step’s output ($node.${ref}) from outside the selection. Include that step or remove the reference.` };
      }
    }
  }
  const keptNodes = nodes.filter((n) => !sel.has(n.id));
  for (const n of keptNodes) {
    const { nodeRefs } = refsOf(n);
    for (const ref of nodeRefs) {
      if (selIdSet.has(ref)) {
        return { ...empty, reason: `A kept node reads a selected step’s output ($node.${ref}). Subflow outputs travel by variable, not node output — include the reader or rework it.` };
      }
    }
  }

  // Variable I/O across the boundary.
  const writtenInside = new Set<string>();
  const readInside = new Set<string>();
  for (const n of selNodes) {
    for (const w of writesOf(n)) writtenInside.add(w);
    for (const r of refsOf(n).varRoots) readInside.add(r);
  }
  // Inputs: roots read inside but not produced inside (incl. ambient run-context roots like `signal`).
  const inputs = [...readInside].filter((r) => !writtenInside.has(r)).sort();

  // Outputs: roots written inside that any kept node reads (writes nothing reads => no need to export).
  const readByKept = new Set<string>();
  for (const n of keptNodes) for (const r of refsOf(n).varRoots) readByKept.add(r);
  const outputs = [...writtenInside].filter((r) => readByKept.has(r)).sort();

  return { ok: true, entryNodeId, exitNodeId, entryEdges, exitEdges, internalEdges, inputs, outputs };
}

export interface ExtractIds {
  /** Id for the new `subflow` call node placed in the parent. */
  callNodeId: string;
  /** Ids for the child's synthetic start/end nodes. */
  startId: string;
  endId: string;
  /** Stable factory for fresh edge ids (e.g. createNodeId('e')) — called once per new edge. */
  newEdgeId: () => string;
}

export interface ExtractPlan {
  /** The subflow's content: start (interfaceInputs) + the selected nodes + end (interfaceOutputs). */
  child: { nodes: ExNode[]; edges: ExEdge[] };
  interfaceInputs: VarRef[];
  interfaceOutputs: VarRef[];
  /** Properties to stamp on the parent call node (subflowId is filled in by the caller after the child is created). */
  callProps: {
    subflowInputs: { target: string; value: string }[];
    subflowOutputs: { source: string; target: string }[];
  };
  /** Parent mutation: drop these nodes/edges, add the call node + these edges. */
  nodesToRemove: string[];
  parentEdgesToRemove: string[];
  parentEdgesToAdd: ExEdge[];
}

/**
 * Plan a behavior-preserving extraction of an already-validated selection. Pure: it returns the pieces
 * to apply (child workflow content, call-node props, parent rewrite); the caller owns id generation,
 * positions, persistence and undo. Throws if the analysis is not `ok` (callers must check first).
 */
export function planExtraction(
  nodes: ExNode[],
  _edges: ExEdge[],
  selectedIds: string[],
  analysis: ExtractAnalysis,
  ids: ExtractIds,
): ExtractPlan {
  if (!analysis.ok || !analysis.entryNodeId) {
    throw new Error('planExtraction called on an un-extractable selection.');
  }
  const sel = new Set(selectedIds);
  const selNodes = nodes.filter((n) => sel.has(n.id));

  const start: ExNode = {
    id: ids.startId,
    type: 'start',
    properties: { interfaceInputs: analysis.inputs.map((name) => ({ name, type: 'string' })) },
  };
  const end: ExNode = {
    id: ids.endId,
    type: 'end',
    properties: { interfaceOutputs: analysis.outputs.map((name) => ({ name, type: 'string' })) },
  };

  // Child edges: start -> entry, the internal edges as-is, and every internal leaf -> end (so the
  // subflow's end runs after the region completes; a terminal region has its tail as the only leaf).
  const internalSourcesWithSucc = new Set(analysis.internalEdges.map((e) => e.source));
  const leaves = selNodes.filter((n) => !internalSourcesWithSucc.has(n.id)).map((n) => n.id);
  const childEdges: ExEdge[] = [
    { id: ids.newEdgeId(), source: ids.startId, sourceHandle: 'result', target: analysis.entryNodeId, targetHandle: 'in' },
    ...analysis.internalEdges,
    ...leaves.map((leaf) => ({ id: ids.newEdgeId(), source: leaf, sourceHandle: 'result', target: ids.endId, targetHandle: 'in' })),
  ];

  const callProps = {
    subflowInputs: analysis.inputs.map((name) => ({ target: name, value: `{{ $variables.${name} }}` })),
    subflowOutputs: analysis.outputs.map((name) => ({ source: name, target: name })),
  };

  // Parent rewrite: external predecessors now feed the call node's `in`; the call node's `result` feeds
  // the external successors. Internal + boundary edges are removed; selected nodes are removed.
  const parentEdgesToAdd: ExEdge[] = [
    ...analysis.entryEdges.map((e) => ({ id: ids.newEdgeId(), source: e.source, sourceHandle: e.sourceHandle, target: ids.callNodeId, targetHandle: 'in' })),
    ...analysis.exitEdges.map((e) => ({ id: ids.newEdgeId(), source: ids.callNodeId, sourceHandle: 'result', target: e.target, targetHandle: e.targetHandle })),
  ];
  const parentEdgesToRemove = [...analysis.entryEdges, ...analysis.exitEdges, ...analysis.internalEdges].map((e) => e.id);

  return {
    child: { nodes: [start, ...selNodes, end], edges: childEdges },
    interfaceInputs: analysis.inputs.map((name) => ({ name, type: 'string' as const })),
    interfaceOutputs: analysis.outputs.map((name) => ({ name, type: 'string' as const })),
    callProps,
    nodesToRemove: selectedIds,
    parentEdgesToRemove,
    parentEdgesToAdd,
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// Stage 3 — parametrized extraction of N isomorphic regions into ONE subflow.
// Select several structurally-identical chains (e.g. Condition+FireAction differing only in a contact
// number); we diff their properties, promote the differing leaves to subflow parameters, build one
// parametrized subflow, and replace each region with a call node binding that region's own values.
// ─────────────────────────────────────────────────────────────────────────────

type PathSeg = string | number;

function getAtPath(obj: unknown, path: PathSeg[]): unknown {
  let cur: unknown = obj;
  for (const seg of path) {
    if (cur == null || typeof cur !== 'object') return undefined;
    cur = (cur as Record<PathSeg, unknown>)[seg];
  }
  return cur;
}

/** Clone-and-set: return a structural copy of `obj` with `path` set to `value` (objects/arrays copied along the way). */
function setAtPath<T>(obj: T, path: PathSeg[], value: unknown): T {
  if (path.length === 0) return value as T;
  const [head, ...rest] = path;
  const base: Record<PathSeg, unknown> = Array.isArray(obj)
    ? [...(obj as unknown[])] as unknown as Record<PathSeg, unknown>
    : { ...(obj as Record<PathSeg, unknown>) };
  base[head] = setAtPath((obj as Record<PathSeg, unknown>)?.[head], rest, value);
  return base as unknown as T;
}

/** Enumerate the leaf paths of a value (primitive / null / empty container = leaf). Skips `_metadata`. */
function leafPaths(value: unknown, prefix: PathSeg[] = []): PathSeg[][] {
  if (Array.isArray(value)) {
    if (value.length === 0) return [prefix];
    return value.flatMap((v, i) => leafPaths(v, [...prefix, i]));
  }
  if (value && typeof value === 'object') {
    const keys = Object.keys(value as Record<string, unknown>).filter((k) => k !== '_metadata');
    if (keys.length === 0) return [prefix];
    return keys.flatMap((k) => leafPaths((value as Record<string, unknown>)[k], [...prefix, k]));
  }
  return [prefix];
}

const eq = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

/** Connected components of the selection over its internal (both-ends-selected) edges. */
export function partitionRegions(selectedIds: string[], edges: ExEdge[]): string[][] {
  const sel = new Set(selectedIds);
  const adj = new Map<string, string[]>();
  for (const id of selectedIds) adj.set(id, []);
  for (const e of edges) {
    if (sel.has(e.source) && sel.has(e.target)) {
      adj.get(e.source)!.push(e.target);
      adj.get(e.target)!.push(e.source);
    }
  }
  const seen = new Set<string>();
  const regions: string[][] = [];
  for (const id of selectedIds) {
    if (seen.has(id)) continue;
    const comp: string[] = [];
    const stack = [id];
    seen.add(id);
    while (stack.length) {
      const cur = stack.pop()!;
      comp.push(cur);
      for (const nb of adj.get(cur) ?? []) if (!seen.has(nb)) { seen.add(nb); stack.push(nb); }
    }
    regions.push(comp);
  }
  return regions;
}

/** Linear node order of a region from its entry, or null when it isn't a single covering path. */
export function linearOrder(regionIds: string[], internalEdges: ExEdge[], entryNodeId: string): string[] | null {
  const within = new Set(regionIds);
  const succ = new Map<string, string[]>();
  for (const e of internalEdges) {
    if (within.has(e.source) && within.has(e.target)) {
      (succ.get(e.source) ?? succ.set(e.source, []).get(e.source)!).push(e.target);
    }
  }
  const order: string[] = [];
  const visited = new Set<string>();
  let cur: string | undefined = entryNodeId;
  while (cur) {
    if (visited.has(cur)) return null;
    visited.add(cur);
    order.push(cur);
    const next: string[] = succ.get(cur) ?? [];
    if (next.length > 1) return null; // branch — not a simple chain (v1)
    cur = next[0];
  }
  return order.length === regionIds.length ? order : null;
}

export interface ParamSpec {
  name: string;
  /** Index into the canonical node order the parameter belongs to. */
  orderIndex: number;
  path: PathSeg[];
  /** The literal value at this path for each region, aligned to the regions array. */
  valuesByRegion: unknown[];
}

export interface RegionInfo {
  ids: string[];
  order: string[];
  analysis: ExtractAnalysis;
}

export interface MultiExtractAnalysis {
  ok: boolean;
  reason?: string;
  regions: RegionInfo[];
  /** Node types in canonical order (the shared structure). */
  signature: string[];
  params: ParamSpec[];
  inputs: string[];
  outputs: string[];
}

function identifierize(seg: PathSeg): string {
  const s = String(seg).replace(/[^A-Za-z0-9_]/g, '');
  return s.length ? s[0].toLowerCase() + s.slice(1) : 'param';
}

function lastIdentifier(s: string): string | null {
  const ids = s.match(/[A-Za-z_][A-Za-z0-9_]*/g);
  return ids && ids.length ? ids[ids.length - 1] : null;
}

const GENERIC_KEYS = /^(value|a|b|ref|kind|type|param|message|data)$/i;

/** A differing leaf inside a Condition operand `{kind:'lit', type, value}` (path ends `…/value`, parent kind=='lit'). */
function isConditionOperandValue(props: Record<string, unknown>, path: PathSeg[]): boolean {
  if (path[path.length - 1] !== 'value') return false;
  const parent = getAtPath(props, path.slice(0, -1)) as { kind?: unknown } | undefined;
  return !!parent && parent.kind === 'lit';
}

/** Pick a readable param name for a differing leaf — prefer a Condition operand's compared field name. */
function chooseParamName(props: Record<string, unknown>, path: PathSeg[]): string {
  if (isConditionOperandValue(props, path)) {
    const cmp = getAtPath(props, path.slice(0, -2)) as { a?: { ref?: unknown; value?: unknown } } | undefined;
    const aRef = cmp?.a?.ref ?? cmp?.a?.value;
    if (typeof aRef === 'string') { const id = lastIdentifier(aRef); if (id) return identifierize(id); }
  }
  const lastStr = [...path].reverse().find((s) => typeof s === 'string');
  return identifierize((lastStr as string | undefined) ?? 'param');
}

/**
 * Validate a multi-region selection and compute the parameters that differ across the regions. Each
 * region must be SESE (per `analyzeExtraction`), a simple chain, and structurally identical (same node
 * type sequence) to the others. Single-region selections pass through with no params.
 */
export function analyzeMultiExtraction(nodes: ExNode[], edges: ExEdge[], selectedIds: string[]): MultiExtractAnalysis {
  const fail = (reason: string): MultiExtractAnalysis => ({ ok: false, reason, regions: [], signature: [], params: [], inputs: [], outputs: [] });
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const regionIdLists = partitionRegions(selectedIds, edges);
  if (regionIdLists.length === 0) return fail('Select at least one node to extract.');

  const regions: RegionInfo[] = [];
  for (const ids of regionIdLists) {
    const analysis = analyzeExtraction(nodes, edges, ids);
    if (!analysis.ok) return fail(analysis.reason ?? 'A selected region can’t be extracted.');
    const order = linearOrder(ids, analysis.internalEdges, analysis.entryNodeId!);
    if (!order) return fail('Each selected region must be a simple chain (no internal branches) to parametrize together.');
    regions.push({ ids, order, analysis });
  }

  const signature = regions[0].order.map((id) => byId.get(id)!.type);
  for (const r of regions.slice(1)) {
    const sig = r.order.map((id) => byId.get(id)!.type);
    if (sig.length !== signature.length || sig.some((t, i) => t !== signature[i])) {
      return fail('The selected chains aren’t structurally identical (different node types), so they can’t share one subflow.');
    }
  }

  // Diff properties node-by-node along the canonical order; differing leaves become parameters.
  const params: ParamSpec[] = [];
  const usedNames = new Set<string>();
  for (let k = 0; k < signature.length; k++) {
    const node0 = byId.get(regions[0].order[k])!;
    for (const path of leafPaths(node0.properties)) {
      const v0 = getAtPath(node0.properties, path);
      const valuesByRegion = regions.map((r) => getAtPath(byId.get(r.order[k])!.properties, path));
      if (valuesByRegion.every((v) => eq(v, v0))) continue; // same everywhere → stays literal
      // Readable, deduped name (generic keys like `value`/`b` fall back to paramN).
      let base = chooseParamName(node0.properties, path);
      if (GENERIC_KEYS.test(base)) base = `param${params.length + 1}`;
      let name = base;
      let n = 2;
      while (usedNames.has(name)) name = `${base}${n++}`;
      usedNames.add(name);
      params.push({ name, orderIndex: k, path, valuesByRegion });
    }
  }

  const inputs = [...new Set(regions.flatMap((r) => r.analysis.inputs))].sort();
  const outputs = [...new Set(regions.flatMap((r) => r.analysis.outputs))].sort();
  return { ok: true, regions, signature, params, inputs, outputs };
}

export interface SubflowCall {
  callNodeId: string;
  /** The region's node ids (so the caller can position the call node and remove them). */
  regionIds: string[];
  subflowInputs: { target: string; value: unknown }[];
  subflowOutputs: { source: string; target: string }[];
  edgesToAdd: ExEdge[];
}

export interface ParametrizedPlan {
  child: { nodes: ExNode[]; edges: ExEdge[] };
  interfaceInputs: VarRef[];
  interfaceOutputs: VarRef[];
  params: ParamSpec[];
  calls: SubflowCall[];
  nodesToRemove: string[];
  parentEdgesToRemove: string[];
}

/**
 * Plan a parametrized extraction: ONE subflow (canonical chain with differing leaves replaced by
 * `{{ $variables.<param> }}`) plus one call node per region binding that region's literal values.
 * `gen(type)` mints node ids; `newEdgeId()` mints edge ids. Throws if `multi` is not ok.
 */
export function planParametrizedExtraction(
  nodes: ExNode[],
  multi: MultiExtractAnalysis,
  gen: (type: string) => string,
  newEdgeId: () => string,
): ParametrizedPlan {
  if (!multi.ok) throw new Error('planParametrizedExtraction called on an un-extractable selection.');
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const base = multi.regions[0];

  // Canonical child chain: clone region-0's nodes, fresh ids, substitute param paths with expressions.
  const childIds = base.order.map((id) => gen(byId.get(id)!.type));
  const childNodes: ExNode[] = base.order.map((origId, k) => {
    const orig = byId.get(origId)!;
    let props: Record<string, unknown> = { ...orig.properties };
    for (const p of multi.params) {
      if (p.orderIndex !== k) continue;
      if (isConditionOperandValue(props, p.path)) {
        // A Condition operand can't hold a `{{…}}` string in its `value`; swap the whole operand to a ref.
        const opPath = p.path.slice(0, -1);
        const operand = getAtPath(props, opPath) as { type?: unknown };
        props = setAtPath(props, opPath, { kind: 'ref', ref: `{{ $variables.${p.name} }}`, type: operand?.type ?? 'string' });
      } else {
        props = setAtPath(props, p.path, `{{ $variables.${p.name} }}`);
      }
    }
    return { id: childIds[k], type: orig.type, properties: props };
  });

  const startId = gen('start');
  const endId = gen('end');
  const interfaceInputs: VarRef[] = [
    ...multi.inputs.map((name) => ({ name, type: 'string' as const })),
    ...multi.params.map((p) => ({ name: p.name, type: 'string' as const })),
  ];
  const start: ExNode = { id: startId, type: 'start', properties: { interfaceInputs } };
  const end: ExNode = { id: endId, type: 'end', properties: { interfaceOutputs: multi.outputs.map((name) => ({ name, type: 'string' })) } };

  const childEdges: ExEdge[] = [
    { id: newEdgeId(), source: startId, sourceHandle: 'result', target: childIds[0], targetHandle: 'in' },
    ...childIds.slice(0, -1).map((id, i) => ({ id: newEdgeId(), source: id, sourceHandle: 'result', target: childIds[i + 1], targetHandle: 'in' })),
    { id: newEdgeId(), source: childIds[childIds.length - 1], sourceHandle: 'result', target: endId, targetHandle: 'in' },
  ];

  const calls: SubflowCall[] = multi.regions.map((region, ri) => {
    const callNodeId = gen('subflow');
    const subflowInputs = [
      ...multi.inputs.map((name) => ({ target: name, value: `{{ $variables.${name} }}` })),
      ...multi.params.map((p) => ({ target: p.name, value: p.valuesByRegion[ri] })),
    ];
    const subflowOutputs = multi.outputs.map((name) => ({ source: name, target: name }));
    const edgesToAdd: ExEdge[] = [
      ...region.analysis.entryEdges.map((e) => ({ id: newEdgeId(), source: e.source, sourceHandle: e.sourceHandle, target: callNodeId, targetHandle: 'in' })),
      ...region.analysis.exitEdges.map((e) => ({ id: newEdgeId(), source: callNodeId, sourceHandle: 'result', target: e.target, targetHandle: e.targetHandle })),
    ];
    return { callNodeId, regionIds: region.ids, subflowInputs, subflowOutputs, edgesToAdd };
  });

  const nodesToRemove = multi.regions.flatMap((r) => r.ids);
  const parentEdgesToRemove = multi.regions.flatMap((r) =>
    [...r.analysis.entryEdges, ...r.analysis.exitEdges, ...r.analysis.internalEdges].map((e) => e.id),
  );

  return {
    child: { nodes: [start, ...childNodes, end], edges: childEdges },
    interfaceInputs,
    interfaceOutputs: multi.outputs.map((name) => ({ name, type: 'string' as const })),
    params: multi.params,
    calls,
    nodesToRemove,
    parentEdgesToRemove,
  };
}
