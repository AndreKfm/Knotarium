// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

/**
 * Pure helper (Feature #10) that explains *why* a connection drop didn't wire
 * up, so the canvas can show an actionable toast instead of silently doing
 * nothing. Kept free of React Flow so the rules are unit-testable.
 *
 * Mirrors the guard sequence in Canvas `onConnectEnd` for a drag that ended
 * without a precise/snap connection (`connectionState.isValid === false`).
 */

export interface ConnectionDropContext {
  /** Handle the drag started on. We only wire output → node. */
  fromHandleType: 'source' | 'target' | null | undefined;
  fromNodeId: string | null | undefined;
  /** Node the drag was released over, or null when dropped on empty pane. */
  toNodeId: string | null | undefined;
  /** Container nodes (forLoop / parallelForEach) hold children, not body wires. */
  toNodeIsContainer: boolean;
  /** Whether the target node exposes an input port to land on. */
  toNodeHasInput: boolean;
}

/**
 * Returns a short, human-readable reason a drop failed, or `null` when there's
 * nothing to report — either because the drop was over empty space (a normal
 * cancel, not worth nagging about) or because it would in fact connect.
 */
export function connectionFailureReason(ctx: ConnectionDropContext): string | null {
  // Released over empty canvas — a plain cancel, stay quiet.
  if (!ctx.toNodeId) {
    return null;
  }
  if (ctx.fromHandleType !== 'source') {
    return 'Start the connection from an output port.';
  }
  if (ctx.toNodeId === ctx.fromNodeId) {
    return "A node can't connect to itself.";
  }
  if (ctx.toNodeIsContainer) {
    return 'Drop onto a node inside the container, not the container itself.';
  }
  if (!ctx.toNodeHasInput) {
    return 'That node has no input port to connect to.';
  }
  // Everything checks out — the caller will create the wire.
  return null;
}
