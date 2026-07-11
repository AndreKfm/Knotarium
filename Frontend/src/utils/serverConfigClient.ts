import type { ServerConfigInfo, CreateServerConfigRequest } from '../types';

const API_BASE = '/api';

async function handleResponse<T>(res: Response): Promise<T> {
  const text = await res.text();
  let data: unknown;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = text;
  }
  if (!res.ok) {
    const msg =
      typeof data === 'object' && data !== null && 'message' in data
        ? String((data as any).message)
        : 'API request failed';
    throw new Error(msg);
  }
  return data as T;
}

export async function listServerConfigs(): Promise<ServerConfigInfo[]> {
  const res = await fetch(`${API_BASE}/server-configs`);
  return handleResponse<ServerConfigInfo[]>(res);
}

export async function createServerConfig(req: CreateServerConfigRequest): Promise<ServerConfigInfo> {
  const res = await fetch(`${API_BASE}/server-configs`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(req),
  });
  return handleResponse<ServerConfigInfo>(res);
}

export async function updateServerConfig(id: string, req: CreateServerConfigRequest): Promise<ServerConfigInfo> {
  const res = await fetch(`${API_BASE}/server-configs/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(req),
  });
  return handleResponse<ServerConfigInfo>(res);
}

export async function deleteServerConfig(id: string): Promise<void> {
  const res = await fetch(`${API_BASE}/server-configs/${id}`, {
    method: 'DELETE',
  });
  return handleResponse<void>(res);
}
