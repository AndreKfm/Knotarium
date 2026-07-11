import { useState } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { HttpRequestPropertyForm } from '../components/HttpRequestPropertyForm';
import { api } from '../utils/api';

vi.mock('../utils/api', () => ({
  api: {
    getCredentials: vi.fn().mockResolvedValue([{ id: 'c1', name: 'My Cred' }]),
    saveCredential: vi.fn().mockResolvedValue({}),
  },
}));

// Controlled wrapper so onChange actually drives the props back in (the form is fully controlled).
function Harness({ initial = {} as Record<string, unknown> }) {
  const [props, setProps] = useState<Record<string, unknown>>(initial);
  return <HttpRequestPropertyForm properties={props} onChange={setProps} />;
}

describe('HttpRequestPropertyForm', () => {
  beforeEach(() => vi.clearAllMocks());

  it('shows the core fields and defaults auth to none (no credential picker)', () => {
    render(<Harness />);
    expect(screen.getByText('URL')).toBeInTheDocument();
    expect(screen.getByText('Method')).toBeInTheDocument();
    expect(screen.getByText('Authentication')).toBeInTheDocument();
    expect(screen.queryByText('Credential (secret)')).not.toBeInTheDocument();
  });

  it('reveals the credential picker for Bearer, and header fields for API key', async () => {
    render(<Harness />);
    // The auth-type <select> is the one defaulting to 'none' (the Method select defaults to 'GET').
    const authSelect = screen.getAllByRole('combobox').find(
      (s) => (s as HTMLSelectElement).value === 'none',
    ) as HTMLSelectElement;

    fireEvent.change(authSelect, { target: { value: 'bearer' } });
    expect(await screen.findByText('Credential (secret)')).toBeInTheDocument();
    expect(screen.queryByText('Header name')).not.toBeInTheDocument();

    fireEvent.change(authSelect, { target: { value: 'apiKey' } });
    expect(await screen.findByText('Header name')).toBeInTheDocument();
    expect(screen.getByText('Value prefix (optional)')).toBeInTheDocument();
    expect(screen.getByText('Credential (secret)')).toBeInTheDocument();
  });

  it('creates a credential inline and selects it', async () => {
    render(<Harness initial={{ authType: 'bearer' }} />);
    // wait for the credential list to load
    await waitFor(() => expect(api.getCredentials).toHaveBeenCalled());

    fireEvent.click(screen.getByRole('button', { name: 'New' }));
    fireEvent.change(screen.getByPlaceholderText(/^Name/), { target: { value: 'Stripe Key' } });
    fireEvent.change(screen.getByPlaceholderText('Secret value'), { target: { value: 'sk-123' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save credential' }));

    await waitFor(() => expect(api.saveCredential).toHaveBeenCalledTimes(1));
    const [, name, value] = (api.saveCredential as unknown as ReturnType<typeof vi.fn>).mock.calls[0];
    expect(name).toBe('Stripe Key');
    expect(value).toBe('sk-123');
  });
});
