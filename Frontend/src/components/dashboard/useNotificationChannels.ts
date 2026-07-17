// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useEffect, useState } from 'react'
import { api } from '../../utils/api'
import type { NotificationChannel } from '../../types'

export interface NotificationChannels {
  /** Available notification channels for per-workflow failure-alert routing. */
  channels: NotificationChannel[]
}

/**
 * Fetches the notification channels once on mount (for the per-workflow failure-alert routing UI).
 * Extracted from Dashboard.tsx. Non-fatal on failure — the routing UI degrades gracefully.
 */
export function useNotificationChannels(): NotificationChannels {
  const [channels, setChannels] = useState<NotificationChannel[]>([])
  useEffect(() => {
    let cancelled = false
    api.getNotificationChannels()
      .then((list) => { if (!cancelled) setChannels(list) })
      .catch(() => { /* non-fatal: alert routing UI degrades gracefully */ })
    return () => { cancelled = true }
  }, [])
  return { channels }
}
