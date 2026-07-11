/**
 * Returns a React state-updater that swaps in `next` only when it differs
 * (by structural JSON equality) from the previous value. When unchanged it
 * returns the previous reference, so React bails out of the re-render.
 *
 * Use this for interval/poll-driven setState calls that would otherwise
 * replace state with a fresh-but-identical array/object every tick and cause
 * a periodic UI flicker.
 */
export function replaceIfChanged<T>(next: T): (prev: T) => T {
  return (prev: T) => (JSON.stringify(prev) === JSON.stringify(next) ? prev : next);
}
