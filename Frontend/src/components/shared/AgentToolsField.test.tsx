import { useState } from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { AgentToolsField } from './AgentToolsField';
import type { ParameterDefinition } from '../../types';

vi.mock('../../utils/api', () => ({
  api: {
    getWorkflows: vi.fn().mockResolvedValue([
      { id: { value: 'wf-1' }, name: 'Lookup Customer', nodes: [], edges: [] },
      { id: { value: 'wf-2' }, name: 'Send Email', nodes: [], edges: [] },
    ]),
  },
}));

const param: ParameterDefinition = { name: 'tools', type: 'agentTools' };

function Harness({ initial }: { initial?: unknown }) {
  const [value, setValue] = useState<unknown>(initial);
  return (
    <>
      <AgentToolsField param={param} value={value} onChange={setValue} />
      <pre data-testid="value">{JSON.stringify(value ?? null)}</pre>
    </>
  );
}

const emitted = () => JSON.parse(screen.getByTestId('value').textContent || 'null');

describe('AgentToolsField', () => {
  beforeEach(() => vi.clearAllMocks());

  it('adds a tool and edits its fields, persisting the array shape', async () => {
    render(<Harness />);

    fireEvent.click(screen.getByRole('button', { name: '+ Add tool' }));
    expect(emitted()).toHaveLength(1);

    // The workflow dropdown is populated from the mocked API.
    await waitFor(() => expect(screen.getByText('Lookup Customer')).toBeInTheDocument());
    fireEvent.change(screen.getByLabelText('Target workflow'), { target: { value: 'wf-1' } });
    fireEvent.change(screen.getByLabelText('Tool name'), { target: { value: 'lookup' } });
    fireEvent.change(screen.getByLabelText('Tool description'), { target: { value: 'look up a customer' } });

    const val = emitted();
    expect(val[0]).toMatchObject({ workflowId: 'wf-1', name: 'lookup', description: 'look up a customer' });
  });

  it('adds a parameter row with type + required', async () => {
    render(<Harness initial={[{ workflowId: 'wf-1', name: 't', description: '', parameters: [], outputs: [] }]} />);

    fireEvent.click(screen.getByRole('button', { name: '+ Add' }));
    fireEvent.change(screen.getByLabelText('Parameter name'), { target: { value: 'id' } });
    fireEvent.change(screen.getByLabelText('Parameter type'), { target: { value: 'number' } });
    fireEvent.click(screen.getByRole('checkbox'));

    expect(emitted()[0].parameters[0]).toEqual({ name: 'id', type: 'number', required: true });
  });

  it('parses comma-separated outputs into an array', () => {
    render(<Harness initial={[{ workflowId: 'wf-1', name: 't', description: '', parameters: [], outputs: [] }]} />);
    fireEvent.change(screen.getByLabelText('Tool outputs'), { target: { value: 'customer, found' } });
    expect(emitted()[0].outputs).toEqual(['customer', 'found']);
  });

  it('shows validation problems for an invalid tool name', () => {
    render(<Harness initial={[{ workflowId: 'wf-1', name: 'bad name', description: '', parameters: [], outputs: [] }]} />);
    expect(screen.getByText(/is invalid/)).toBeInTheDocument();
  });

  it('drops back to undefined when the last tool is removed', () => {
    render(<Harness initial={[{ workflowId: 'wf-1', name: 't', description: '', parameters: [], outputs: [] }]} />);
    fireEvent.click(screen.getByRole('button', { name: '✕' }));
    expect(emitted()).toBeNull();
  });
});
