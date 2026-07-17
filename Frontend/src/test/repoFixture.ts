// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Test-only helper: read a repo-root fixture (e.g. the shared condition fixtures under test-fixtures/condition/)
// from disk so the FE suites load the SAME files the backend suites do. The tiny Node surface used
// here is typed by src/test/node-shims.d.ts (see that file for why we avoid full @types/node).

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

/** Read + parse a JSON fixture given a path relative to the Frontend/ dir (vitest's cwd). */
export function loadRepoJson<T>(relativeFromFrontend: string): T {
  return JSON.parse(readFileSync(resolve(process.cwd(), relativeFromFrontend), 'utf-8')) as T;
}
