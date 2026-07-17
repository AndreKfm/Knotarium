// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import '@testing-library/jest-dom';

// jsdom has no ResizeObserver; @xyflow/react (used by TemplatePreview and the canvas) needs one.
if (typeof globalThis.ResizeObserver === 'undefined') {
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}
