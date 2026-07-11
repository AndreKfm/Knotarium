import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useState } from 'react';
import { DynamicFieldsField } from './DynamicFieldsField';
import { api } from '../../utils/api';
import type { OptionItem, ParameterDefinition } from '../../types';

vi.mock('../../utils/api', () => ({
  api: { loadNodeOptions: vi.fn() },
}));

const loadNodeOptions = api.loadNodeOptions as unknown as ReturnType<typeof vi.fn>;

function ok(options: OptionItem[]) {
  return { options, hasMore: false, nextPage: null, error: null };
}

const param: ParameterDefinition = {
  name: 'data',
  type: 'dynamicFields',
  optionsLoader: 'reactor.actionFields',
  integrationType: 'generic',
  dependsOn: ['action', 'instance'],
};

/** Harness owns the persisted value and exposes it as JSON so tests can assert the exact shape/types. */
function Harness({ initial }: { initial?: unknown }) {
  const [value, setValue] = useState<unknown>(initial);
  return (
    <>
      <DynamicFieldsField
        param={param}
        value={value}
        properties={{ action: 'SetDigitalOutput', instance: 'default' }}
        onChange={setValue}
      />
      <pre data-testid="value">{JSON.stringify(value ?? null)}</pre>
    </>
  );
}

const readValue = () => screen.getByTestId('value').textContent ?? '';

beforeEach(() => {
  loadNodeOptions.mockReset();
});

describe('DynamicFieldsField', () => {
  it('renders one typed field per option and stores an integer as a number', async () => {
    loadNodeOptions.mockResolvedValue(ok([
      { label: 'Name', value: 'Name', kind: 'String' },
      { label: 'Count', value: 'Count', kind: 'Integer' },
    ]));

    render(<Harness />);
    await waitFor(() => expect(screen.getByText('Name')).toBeInTheDocument());

    fireEvent.change(screen.getByPlaceholderText('Enter Name…'), { target: { value: 'gate' } });
    fireEvent.change(screen.getByPlaceholderText('Enter number…'), { target: { value: '3' } });

    // Integer is stored as a JSON number (no quotes), string keeps quotes.
    await waitFor(() => expect(readValue()).toContain('"Count":3'));
    expect(readValue()).toContain('"Name":"gate"');
  });

  it('offers an enum dropdown from enumValues', async () => {
    loadNodeOptions.mockResolvedValue(ok([
      { label: 'Direction', value: 'Direction', kind: 'Enum', enumValues: ['In', 'Out'] },
    ]));

    render(<Harness />);
    await waitFor(() => expect(screen.getByText('Direction')).toBeInTheDocument());

    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'Out' } });
    await waitFor(() => expect(readValue()).toContain('"Direction":"Out"'));
  });

  it('renders a boolean as a checkbox storing true', async () => {
    loadNodeOptions.mockResolvedValue(ok([{ label: 'Enabled', value: 'Enabled', kind: 'Boolean' }]));

    render(<Harness />);
    await waitFor(() => expect(screen.getByText('Enabled')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('checkbox'));
    await waitFor(() => expect(readValue()).toContain('"Enabled":true'));
  });

  it('preserves unknown/extra keys not described by the schema', async () => {
    loadNodeOptions.mockResolvedValue(ok([{ label: 'Name', value: 'Name', kind: 'String' }]));

    render(<Harness initial={{ Legacy: 'keep', Name: 'old' }} />);
    await waitFor(() => expect(screen.getByText(/Extra keys preserved/)).toBeInTheDocument());

    fireEvent.change(screen.getByPlaceholderText('Enter Name…'), { target: { value: 'new' } });
    await waitFor(() => expect(readValue()).toContain('"Name":"new"'));
    // The unknown key survives the edit.
    expect(readValue()).toContain('"Legacy":"keep"');
  });

  it('falls back to a raw JSON editor when no schema is available', async () => {
    loadNodeOptions.mockResolvedValue(ok([]));

    render(<Harness />);
    // No schema → no field/raw toggle, but the raw JSON textarea is shown.
    await waitFor(() => expect(screen.getByPlaceholderText(/"Key": "value"/)).toBeInTheDocument());
    expect(screen.queryByText('Raw JSON')).not.toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText(/"Key": "value"/), { target: { value: '{"X":1}' } });
    await waitFor(() => expect(readValue()).toContain('"X":1'));
  });

  it('loads a legacy JSON-string value into typed fields', async () => {
    loadNodeOptions.mockResolvedValue(ok([{ label: 'Name', value: 'Name', kind: 'String' }]));

    render(<Harness initial={'{"Name":"legacy"}'} />);
    await waitFor(() => expect(screen.getByText('Name')).toBeInTheDocument());
    expect(screen.getByPlaceholderText('Enter Name…')).toHaveValue('legacy');
  });

  it('binds a field to an expression via the fx toggle', async () => {
    loadNodeOptions.mockResolvedValue(ok([{ label: 'Name', value: 'Name', kind: 'String' }]));

    render(<Harness />);
    await waitFor(() => expect(screen.getByText('Name')).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: 'fx' }));
    const exprInput = screen.getByPlaceholderText(/\{\{ \$node/);
    fireEvent.change(exprInput, { target: { value: '{{ $node.n1.output.id }}' } });

    await waitFor(() => expect(readValue()).toContain('$node.n1.output.id'));
  });

  it('reopens an expression-valued field in fx mode', async () => {
    loadNodeOptions.mockResolvedValue(ok([{ label: 'Name', value: 'Name', kind: 'String' }]));

    render(<Harness initial={{ Name: '{{ $x }}' }} />);
    await waitFor(() => expect(screen.getByText('Name')).toBeInTheDocument());
    // The toggle shows "value" (i.e. currently in expression mode) and the expression input holds it.
    expect(screen.getByRole('button', { name: 'value' })).toBeInTheDocument();
    expect(screen.getByPlaceholderText(/\{\{ \$node/)).toHaveValue('{{ $x }}');
  });

  it('resolves a Channel field to a cascaded picker storing the scalar id', async () => {
    loadNodeOptions.mockImplementation((_it: string, loader: string) => {
      if (loader === 'reactor.actionFields') {
        return Promise.resolve(ok([{ label: 'Media Channel (Channel)', value: 'MediaChannelID', kind: 'Channel' }]));
      }
      if (loader === 'reactor.channels') {
        return Promise.resolve(ok([{ label: 'Front Cam', value: 'ch_1' }, { label: 'Back Cam', value: 'ch_2' }]));
      }
      return Promise.resolve(ok([]));
    });

    render(<Harness />);
    await waitFor(() => expect(screen.getByText('Back Cam')).toBeInTheDocument());

    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'ch_2' } });
    // The stored value is the plain scalar id, not a {value,label} object.
    await waitFor(() => expect(readValue()).toContain('"MediaChannelID":"ch_2"'));
  });

  it('falls back to a text box when the resource catalog is unavailable', async () => {
    loadNodeOptions.mockImplementation((_it: string, loader: string) => {
      if (loader === 'reactor.actionFields') {
        return Promise.resolve(ok([{ label: 'Media Channel (Channel)', value: 'MediaChannelID', kind: 'Channel' }]));
      }
      return Promise.resolve(ok([])); // no channels available at design time
    });

    render(<Harness />);
    await waitFor(() => expect(screen.getByText('Media Channel (Channel)')).toBeInTheDocument());
    expect(screen.getByPlaceholderText('Enter id…')).toBeInTheDocument();
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
  });
});
