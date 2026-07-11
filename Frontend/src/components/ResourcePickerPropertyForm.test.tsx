import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { useState } from 'react';
import { ResourcePickerPropertyForm } from './ResourcePickerPropertyForm';
import * as serverConfigClient from '../utils/serverConfigClient';
import * as openApiClient from '../utils/openApiClient';
import { api } from '../utils/api';

vi.mock('../utils/serverConfigClient', () => ({ listServerConfigs: vi.fn() }));
vi.mock('../utils/openApiClient', () => ({ listSpecs: vi.fn(), getSpecDetail: vi.fn() }));
vi.mock('../utils/api', () => ({ api: { loadNodeOptions: vi.fn() } }));

const mockConfigs = [
  { id: 'cfg-1', name: 'Mock', baseUrl: 'http://127.0.0.1:8787', serverVariables: {}, securitySchemeType: 'none', createdAt: '', updatedAt: '' },
];
const mockSpecs = [{ id: 'mock-pet-stores', title: 'Mock Pet Stores', apiVersion: '1.0', latestVersionNumber: 1, importedAtUtc: '' }];
const mockSpecDetail = {
  id: 'mock-pet-stores', title: 'Mock Pet Stores', schemas: [],
  groups: [{ tag: 'pets', operations: [
    { operationId: 'listPets', method: 'GET', pathTemplate: '/pets', tags: ['pets'], parameters: [] },
    { operationId: 'getPet', method: 'GET', pathTemplate: '/pets/{petId}', tags: ['pets'], parameters: [] },
  ] }],
};

function Harness({ initial }: { initial?: Record<string, unknown> }) {
  const [props, setProps] = useState<Record<string, unknown>>(initial ?? {});
  return <ResourcePickerPropertyForm properties={props} onChange={setProps} />;
}

describe('ResourcePickerPropertyForm', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(serverConfigClient.listServerConfigs).mockResolvedValue(mockConfigs as any);
    vi.mocked(openApiClient.listSpecs).mockResolvedValue(mockSpecs as any);
    vi.mocked(openApiClient.getSpecDetail).mockResolvedValue(mockSpecDetail as any);
    (api.loadNodeOptions as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      options: [{ label: 'Rex', value: 'pet_rex' }], hasMore: false, nextPage: null, error: null,
    });
  });

  it('shows the placeholder until a server config and path are set', async () => {
    render(<Harness />);
    await waitFor(() => expect(screen.getByText('Select server config…')).toBeInTheDocument());
    expect(screen.getByText(/Choose a Server Config and a Collection/i)).toBeInTheDocument();
  });

  it('selecting a server config populates the control', async () => {
    render(<Harness />);
    // Open the Server Config select and pick the option.
    fireEvent.click(await screen.findByText('Select server config…'));
    fireEvent.click(await screen.findByText('Mock'));
    // The control now shows the chosen server with its base url meta.
    await waitFor(() => expect(screen.getByText('http://127.0.0.1:8787')).toBeInTheDocument());
  });

  it('lists collection endpoints from an imported spec, excluding item endpoints', async () => {
    render(<Harness initial={{ pickerSpecId: 'mock-pet-stores' }} />);
    // Open the Collection select.
    fireEvent.click(await screen.findByText('Select a list endpoint…'));
    // The collection menu lists /pets but not the item endpoint /pets/{petId}.
    await waitFor(() => expect(screen.getByText('/pets')).toBeInTheDocument());
    expect(screen.queryByText('getPet')).not.toBeInTheDocument();
  });

  it('renders the resolved-record preview for a saved selection', async () => {
    render(<Harness initial={{ serverConfigId: 'cfg-1', path: 'pets', selection: { value: 'pet_rex', label: 'Rex', mode: 'list' } }} />);
    await waitFor(() => expect(screen.getByText('Resolved record')).toBeInTheDocument());
    expect(screen.getByText('"pet_rex"')).toBeInTheDocument();
  });
});
