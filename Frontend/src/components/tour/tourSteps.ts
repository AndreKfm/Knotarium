export interface TourStep {
  /** CSS selector of the element to spotlight. Omit for a centered, target-less step. */
  selector?: string;
  title: string;
  body: string;
}

// Single source of truth for the guided product tour (rendered by GuidedTour). Data-only so a test
// can assert the set stays sane and the selectors match the data-tour markers in App.tsx.
export const TOUR_STEPS: TourStep[] = [
  {
    title: 'Welcome to Knotarium',
    body: 'A quick tour of the main areas — about a minute. Skip anytime, or restart it later from “Tour” in the top bar.',
  },
  {
    selector: '[data-tour="dashboard"]',
    title: 'Dashboard',
    body: 'Your control center: every workflow, live run stats, and the operations timeline of recent executions.',
  },
  {
    selector: '[data-tour="canvas-editor"]',
    title: 'Canvas Editor',
    body: 'Build workflows visually — drag nodes from the palette, connect their ports, then run, version, and monitor.',
  },
  {
    selector: '[data-tour="templates"]',
    title: 'Templates',
    body: 'Install a ready-to-run starter from the gallery — the fastest way to see a real workflow and learn the editor.',
  },
  {
    selector: '[data-tour="ai-generate"]',
    title: 'AI Generate',
    body: 'Describe what you want in plain language and let AI draft a workflow you can refine on the canvas.',
  },
  {
    selector: '[data-tour="settings"]',
    title: 'Settings & safety',
    body: 'Enable code and database nodes, and grant file-system access here. Both are off by default — turn them on only for instances you trust.',
  },
  {
    selector: '[data-tour="dead-letter"]',
    title: 'Dead Letter',
    body: 'Failed runs collect here so nothing is lost — review, discard, or replay them.',
  },
  {
    title: 'You’re set',
    body: 'Install a sample or create your first workflow to dive in. Press ? anytime for keyboard shortcuts.',
  },
];
