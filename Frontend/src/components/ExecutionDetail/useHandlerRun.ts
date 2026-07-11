import { useEffect, useState } from 'react';
import { api } from '../../utils/api';

export interface HandlerRun {
  id: string;
  status: string;
}

/**
 * Resolves the error-handler run spawned by a failed run. The handler run is created
 * ASYNCHRONOUSLY (the executor enqueues it after the failure), so a one-shot fetch races and
 * intermittently misses it — the cause of the flaky "Error handler run" pill. This polls a few
 * times (only while the run is Failed, since only failures spawn a handler) until it appears,
 * then stops.
 */
export function useHandlerRun(executionId: string, executionStatus?: string): HandlerRun | null {
  const [handlerRun, setHandlerRun] = useState<HandlerRun | null>(null);

  useEffect(() => {
    setHandlerRun(null);

    // Only failed runs spawn a handler; anything else never has one, so don't poll.
    if (executionStatus !== 'Failed') {
      return;
    }

    let cancelled = false;
    let attempts = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const poll = async () => {
      attempts += 1;
      try {
        const run = await api.getExecutionErrorRun(executionId);
        if (cancelled) return;
        if (run) {
          setHandlerRun(run);
          return; // found — stop polling
        }
      } catch {
        /* keep trying */
      }
      // Handler usually appears within ~1-2s of the failure; give it ~15s.
      if (!cancelled && attempts < 10) {
        timer = setTimeout(poll, 1500);
      }
    };

    void poll();
    return () => { cancelled = true; if (timer) clearTimeout(timer); };
  }, [executionId, executionStatus]);

  return handlerRun;
}
