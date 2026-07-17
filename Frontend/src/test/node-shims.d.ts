// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

// Minimal ambient typings for the tiny Node surface our test helpers use (see repoFixture.ts).
// Declaring only this avoids pulling all of @types/node into the app's single type program, which
// would retype DOM globals (e.g. setInterval → NodeJS.Timeout) and break unrelated tests. At run time
// vitest (Node) provides the real implementations; these declarations exist purely for `tsc -b`.

declare module 'node:fs' {
  export function readFileSync(path: string, encoding: 'utf-8'): string;
}

declare module 'node:path' {
  export function resolve(...parts: string[]): string;
}

declare const process: { cwd(): string };
