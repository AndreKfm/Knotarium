// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useCallback, useEffect, useRef, useState } from 'react'

/** A connection-feedback toast: a success pulse, or an error explaining why a drop didn't wire up. */
export type ConnectToast = { kind: 'success' | 'error'; message: string }

export interface ConnectFeedback {
  connectToast: ConnectToast | null
  /** Brief "Connected ✓" confirmation pulse after a successful wire-up. */
  triggerConnectToast: () => void
  /** Explain why a connection drop didn't take. Lingers longer than the success pulse so the reason is readable. */
  triggerConnectError: (message: string) => void
}

/**
 * Owns the transient connection-feedback toast shown at the bottom of the canvas.
 * Extracted from Canvas.tsx — zero coupling to the graph, so it lives on its own.
 */
export function useConnectFeedback(): ConnectFeedback {
  const [connectToast, setConnectToast] = useState<ConnectToast | null>(null)
  const connectToastTimer = useRef<ReturnType<typeof setTimeout> | null>(null)

  const triggerConnectToast = useCallback(() => {
    setConnectToast({ kind: 'success', message: 'Connected ✓' })
    if (connectToastTimer.current) {
      clearTimeout(connectToastTimer.current)
    }
    connectToastTimer.current = setTimeout(() => setConnectToast(null), 1100)
  }, [])

  const triggerConnectError = useCallback((message: string) => {
    setConnectToast({ kind: 'error', message })
    if (connectToastTimer.current) {
      clearTimeout(connectToastTimer.current)
    }
    connectToastTimer.current = setTimeout(() => setConnectToast(null), 2600)
  }, [])

  // Clear any pending timer on unmount so it can't fire setState on a gone component.
  useEffect(() => () => {
    if (connectToastTimer.current) {
      clearTimeout(connectToastTimer.current)
    }
  }, [])

  return { connectToast, triggerConnectToast, triggerConnectError }
}
