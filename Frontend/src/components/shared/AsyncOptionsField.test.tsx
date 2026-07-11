import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useState } from 'react';
import { AsyncOptionsField } from './AsyncOptionsField';
import { api } from '../../utils/api';
import type { ParameterDefinition } from '../../types';

vi.mock('../../utils/api', () => ({
  api: { loadNodeOptions: vi.fn() },
}));

const loadNodeOptions = api.loadNodeOptions as unknown as ReturnType<typeof vi.fn>;

function ok(options: Array<{ label: string; value: string }>) {
  return { options, hasMore: false, nextPage: null, error: null };
}

/** Test harness that owns the persisted value so onChange round-trips like the real form. */
function Harness({ param, parent }: { param: ParameterDefinition; parent?: string }) {
  const [value, setValue] = useState<unknown>(undefined);
  const properties: Record<string, unknown> = parent !== undefined ? { region: parent } : {};
  return (
    <AsyncOptionsField
      param={param}
      value={value}
      properties={properties}
      connectionId="srv1"
      onChange={setValue}
    />
  );
}

const baseParam: ParameterDefinition = {
  name: 'location',
  type: 'dynamicOptions',
  optionsLoader: 'rest.collection',
  integrationType: 'generic',
};

beforeEach(() => {
  loadNodeOptions.mockReset();
});

describe('AsyncOptionsField', () => {
  it('loads options on open and selects one (single)', async () => {
    loadNodeOptions.mockResolvedValue(ok([
      { label: 'Front Office', value: 'res_7f3a' },
      { label: 'Warehouse', value: 'res_22b1' },
    ]));

    render(<Harness param={baseParam} />);
    fireEvent.click(screen.getByText('Select…'));

    await waitFor(() => expect(screen.getByText('Front Office')).toBeInTheDocument());
    fireEvent.click(screen.getByText('Front Office'));

    // Summary now shows the chosen label.
    await waitFor(() => expect(screen.getByText('Front Office')).toBeInTheDocument());
    expect(loadNodeOptions).toHaveBeenCalled();
  });

  it('multi-select shows a chip per pick and keeps the list open', async () => {
    loadNodeOptions.mockResolvedValue(ok([
      { label: 'Apple', value: 'a' },
      { label: 'Banana', value: 'b' },
    ]));

    render(<Harness param={{ ...baseParam, multiple: true }} />);
    fireEvent.click(screen.getByText('Select…'));

    await waitFor(() => expect(screen.getByText('Apple')).toBeInTheDocument());
    fireEvent.click(screen.getByText('Apple'));
    fireEvent.click(screen.getByText('Banana'));

    // Both selected -> summary count reflects 2.
    await waitFor(() => expect(screen.getByText('2 selected')).toBeInTheDocument());
  });

  it('shows an error and a manual-entry fallback when the system is unreachable', async () => {
    loadNodeOptions.mockResolvedValue({
      options: [], hasMore: false, nextPage: null,
      error: { code: 'SYSTEM_UNREACHABLE', message: 'offline' },
    });

    render(<Harness param={{ ...baseParam, allowManualEntry: true }} />);
    fireEvent.click(screen.getByText('Select…'));

    await waitFor(() => expect(screen.getByText('offline')).toBeInTheDocument());
    const manual = screen.getByPlaceholderText('Enter value manually…');
    fireEvent.change(manual, { target: { value: 'manual-id' } });
    fireEvent.click(screen.getByText('Add'));

    await waitFor(() => expect(screen.getByText('manual-id')).toBeInTheDocument());
  });

  it('clears a child selection when the parent dependsOn value changes', async () => {
    loadNodeOptions.mockResolvedValue(ok([{ label: 'City Hall', value: 'c1' }]));

    const param: ParameterDefinition = { ...baseParam, dependsOn: ['region'] };

    function CascadeHarness() {
      const [value, setValue] = useState<unknown>({ value: 'c1', label: 'City Hall', mode: 'list' });
      const [region, setRegion] = useState('north');
      return (
        <div>
          <button onClick={() => setRegion('south')}>change-region</button>
          <AsyncOptionsField
            param={param}
            value={value}
            properties={{ region }}
            connectionId="srv1"
            onChange={setValue}
          />
        </div>
      );
    }

    render(<CascadeHarness />);
    // Initially the saved selection is shown.
    expect(screen.getByText('City Hall')).toBeInTheDocument();

    fireEvent.click(screen.getByText('change-region'));

    // Parent changed -> child selection cleared back to placeholder.
    await waitFor(() => expect(screen.getByText('Select…')).toBeInTheDocument());
  });
});
