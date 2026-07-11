import type { CompilationDiagnostic } from '../types';

export interface ApiError {
  status?: number;
  message?: string;
  data?: unknown;
}

export function isApiError(error: unknown): error is ApiError {
  return typeof error === 'object' && error !== null;
}

export function getErrorMessage(error: unknown, fallback: string): string {
  return isApiError(error) && typeof error.message === 'string' ? error.message : fallback;
}

export function getErrorDiagnostics(error: unknown): CompilationDiagnostic[] | null {
  if (!isApiError(error) || !error.data || typeof error.data !== 'object' || !('diagnostics' in error.data)) {
    return null;
  }
  const diagnostics = (error.data as { diagnostics: unknown }).diagnostics;
  return Array.isArray(diagnostics) ? (diagnostics as CompilationDiagnostic[]) : null;
}
