// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import type { ImportedSpec, SpecDetail, ApiOperation, ServerConfigInfo, LocatorSuggestion } from '../types';

const API_BASE = '/api';

async function handleResponse<T>(res: Response): Promise<T> {
  const text = await res.text();
  let data: unknown;
  try {
    data = text ? (JSON.parse(text) as unknown) : null;
  } catch {
    data = text;
  }
  if (!res.ok) {
    const msg =
      typeof data === 'object' && data !== null && 'message' in data
        ? String((data as { message: unknown }).message)
        : 'API request failed';
    throw new Error(msg);
  }
  return data as T;
}

interface RawSpecSummary {
  id: string;
  title: string;
  apiVersion: string;
  latestVersionNumber: number;
  importedAtUtc: string;
  originalFormat: string;
}

interface RawImportResult {
  id: string;
  versionNumber: number;
  title: string;
  originalFormat: string;
  groups: SpecDetail['groups'];
  schemas: SpecDetail['schemas'];
}

function mapSummary(raw: RawSpecSummary): ImportedSpec {
  return {
    id: raw.id,
    title: raw.title,
    apiVersion: raw.apiVersion,
    latestVersionNumber: raw.latestVersionNumber,
    importedAtUtc: raw.importedAtUtc,
  };
}

function mapDetail(raw: RawImportResult): SpecDetail {
  return {
    id: raw.id,
    title: raw.title,
    groups: raw.groups ?? [],
    schemas: raw.schemas ?? [],
    defaultServers: (raw as any).defaultServers ?? [],
  };
}

export async function listSpecs(): Promise<ImportedSpec[]> {
  const res = await fetch(`${API_BASE}/openapi/specs`);
  const data = await handleResponse<RawSpecSummary[]>(res);
  return data.map(mapSummary);
}

export async function importSpec(content: string | File, specId?: string): Promise<ImportedSpec> {
  const trimmedId = specId?.trim();
  let res: Response;
  if (content instanceof File) {
    const form = new FormData();
    form.append('file', content);
    if (trimmedId) form.append('specId', trimmedId);
    res = await fetch(`${API_BASE}/openapi/specs`, { method: 'POST', body: form });
  } else {
    res = await fetch(`${API_BASE}/openapi/specs`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(trimmedId ? { content, specId: trimmedId } : { content }),
    });
  }
  const raw = await handleResponse<RawImportResult>(res);
  return {
    id: raw.id,
    title: raw.title,
    apiVersion: raw.originalFormat,
    latestVersionNumber: raw.versionNumber,
    importedAtUtc: new Date().toISOString(),
  };
}

export async function importSpecFromUrl(url: string, specId?: string, allowInsecureCertificate = false): Promise<ImportedSpec> {
  const trimmedId = specId?.trim();
  const body: Record<string, unknown> = { url };
  if (trimmedId) body.specId = trimmedId;
  if (allowInsecureCertificate) body.allowInsecureCertificate = true;
  const res = await fetch(`${API_BASE}/openapi/specs`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  const raw = await handleResponse<RawImportResult>(res);
  return {
    id: raw.id,
    title: raw.title,
    apiVersion: raw.originalFormat,
    latestVersionNumber: raw.versionNumber,
    importedAtUtc: new Date().toISOString(),
  };
}

export async function getSpecDetail(id: string): Promise<SpecDetail> {
  const res = await fetch(`${API_BASE}/openapi/specs/${id}`);
  const raw = await handleResponse<RawImportResult>(res);
  return mapDetail(raw);
}

export async function getOperation(specId: string, operationId: string): Promise<ApiOperation> {
  const res = await fetch(`${API_BASE}/openapi/specs/${specId}/operations/${operationId}`);
  return handleResponse<ApiOperation>(res);
}

export async function getLocatorSuggestions(specId: string, operationId: string): Promise<LocatorSuggestion[]> {
  const res = await fetch(`${API_BASE}/openapi/specs/${specId}/operations/${operationId}/locator-suggestions`);
  return handleResponse<LocatorSuggestion[]>(res);
}

export async function deleteSpec(id: string): Promise<void> {
  const res = await fetch(`${API_BASE}/openapi/specs/${id}`, { method: 'DELETE' });
  if (!res.ok) await handleResponse<never>(res);
}

export async function listServerConfigs(): Promise<ServerConfigInfo[]> {
  const res = await fetch(`${API_BASE}/server-configs`);
  return handleResponse<ServerConfigInfo[]>(res);
}
