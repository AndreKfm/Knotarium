// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { render, screen, fireEvent } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { RestCallerPropertyForm } from './RestCallerPropertyForm';

vi.mock('../utils/openApiClient', () => ({
  getOperation: vi.fn(),
  listServerConfigs: vi.fn(),
  getSpecDetail: vi.fn(),
  getLocatorSuggestions: vi.fn(),
}));

import * as client from '../utils/openApiClient';

const mockSpecDetail = {
  id: 'petstore',
  title: 'Petstore',
  groups: [
    {
      tag: 'pets',
      operations: [
        {
          operationId: 'getPetById',
          method: 'GET',
          pathTemplate: '/pets/{id}',
          tags: ['pets'],
          parameters: [
            {
              name: 'id',
              in: 'path',
              required: true,
              description: 'Pet ID',
              schemaJson: '{"type":"string"}',
            },
          ],
        },
        {
          operationId: 'listPets',
          method: 'GET',
          pathTemplate: '/pets',
          tags: ['pets'],
          parameters: [
            {
              name: 'status',
              in: 'query',
              required: false,
              description: 'Pet Status',
              schemaJson: '{"type":"string"}',
            },
          ],
        },
      ],
    },
  ],
  schemas: [],
};

const mockGetPetByIdOp = {
  operationId: 'getPetById',
  method: 'GET',
  pathTemplate: '/pets/{id}',
  parameters: [
    {
      name: 'id',
      in: 'path',
      required: true,
      description: 'Pet ID',
      schemaJson: '{"type":"string"}',
    },
  ],
};

const mockListPetsOp = {
  operationId: 'listPets',
  method: 'GET',
  pathTemplate: '/pets',
  parameters: [
    {
      name: 'status',
      in: 'query',
      required: false,
      description: 'Pet Status',
      schemaJson: '{"type":"string"}',
    },
  ],
};

const mockServerConfigs = [
  {
    id: 'cfg-1',
    name: 'Development',
    baseUrl: 'https://dev.example.com',
    serverVariables: {},
    securitySchemeType: 'none',
    createdAt: '',
    updatedAt: '',
  },
  {
    id: 'cfg-2',
    name: 'Production',
    baseUrl: 'https://api.example.com',
    serverVariables: {},
    securitySchemeType: 'none',
    createdAt: '',
    updatedAt: '',
  },
];

describe('RestCallerPropertyForm', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(client.getSpecDetail).mockResolvedValue(mockSpecDetail as any);
    vi.mocked(client.listServerConfigs).mockResolvedValue(mockServerConfigs as any);
    vi.mocked(client.getLocatorSuggestions).mockResolvedValue([]);
  });

  it('renders_path_params_for_operation', async () => {
    vi.mocked(client.getOperation).mockResolvedValueOnce(mockGetPetByIdOp as any);

    render(
      <RestCallerPropertyForm
        specId="petstore"
        operationId="getPetById"
        arguments={{}}
        onArgumentsChange={vi.fn()}
        onOperationIdChange={vi.fn()}
        onServerConfigIdChange={vi.fn()}
      />
    );

    expect(await screen.findByText('Path Parameters')).toBeInTheDocument();
    expect(screen.getByText('id')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Enter id...')).toBeInTheDocument();
  });

  it('renders_query_params_for_operation', async () => {
    vi.mocked(client.getOperation).mockResolvedValueOnce(mockListPetsOp as any);

    render(
      <RestCallerPropertyForm
        specId="petstore"
        operationId="listPets"
        arguments={{}}
        onArgumentsChange={vi.fn()}
        onOperationIdChange={vi.fn()}
        onServerConfigIdChange={vi.fn()}
      />
    );

    expect(await screen.findByText('Query Parameters')).toBeInTheDocument();
    expect(screen.getByText('status')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Enter status...')).toBeInTheDocument();
  });

  it('required_param_is_marked', async () => {
    vi.mocked(client.getOperation).mockResolvedValueOnce(mockGetPetByIdOp as any);

    render(
      <RestCallerPropertyForm
        specId="petstore"
        operationId="getPetById"
        arguments={{}}
        onArgumentsChange={vi.fn()}
        onOperationIdChange={vi.fn()}
        onServerConfigIdChange={vi.fn()}
      />
    );

    const label = await screen.findByText((_, element) => {
      if (!element || element.tagName.toLowerCase() !== 'label') return false;
      const text = (element.textContent || '').trim();
      return text.startsWith('id') && text.includes('*');
    });
    expect(label).toBeInTheDocument();
  });

  it('optional_param_not_marked', async () => {
    vi.mocked(client.getOperation).mockResolvedValueOnce(mockListPetsOp as any);

    render(
      <RestCallerPropertyForm
        specId="petstore"
        operationId="listPets"
        arguments={{}}
        onArgumentsChange={vi.fn()}
        onOperationIdChange={vi.fn()}
        onServerConfigIdChange={vi.fn()}
      />
    );

    const label = await screen.findByText((_, element) => {
      if (!element || element.tagName.toLowerCase() !== 'label') return false;
      const text = (element.textContent || '').trim();
      return text.startsWith('status') && !text.includes('*');
    });
    expect(label).toBeInTheDocument();
  });

  it('changing_operationId_reloads_form', async () => {
    vi.mocked(client.getOperation).mockResolvedValue(mockGetPetByIdOp as any);
    const onOperationIdChange = vi.fn();

    render(
      <RestCallerPropertyForm
        specId="petstore"
        operationId="getPetById"
        arguments={{}}
        onArgumentsChange={vi.fn()}
        onOperationIdChange={onOperationIdChange}
        onServerConfigIdChange={vi.fn()}
      />
    );

    // Wait for initial load
    await screen.findByText('Path Parameters');
    expect(client.getOperation).toHaveBeenCalledWith('petstore', 'getPetById');

    // Open the custom operation picker (closed control shows the selected operationId)
    fireEvent.click(screen.getByText('getPetById'));
    // Pick a different operation from the menu by its path (listPets -> /pets)
    fireEvent.click(screen.getByText('/pets'));

    expect(onOperationIdChange).toHaveBeenCalledWith('listPets');
  });

  it('onArgumentsChange_called_on_input', async () => {
    vi.mocked(client.getOperation).mockResolvedValueOnce(mockGetPetByIdOp as any);
    const onArgumentsChange = vi.fn();

    render(
      <RestCallerPropertyForm
        specId="petstore"
        operationId="getPetById"
        arguments={{}}
        onArgumentsChange={onArgumentsChange}
        onOperationIdChange={vi.fn()}
        onServerConfigIdChange={vi.fn()}
      />
    );

    const input = await screen.findByPlaceholderText('Enter id...');
    fireEvent.change(input, { target: { value: '123' } });

    expect(onArgumentsChange).toHaveBeenCalledWith({
      path: { id: '123' }
    });
  });

  it('toggling_pick_from_list_persists_locator_config', async () => {
    vi.mocked(client.getOperation).mockResolvedValueOnce(mockGetPetByIdOp as any);
    const onArgumentsChange = vi.fn();

    render(
      <RestCallerPropertyForm
        specId="petstore"
        operationId="getPetById"
        arguments={{}}
        onArgumentsChange={onArgumentsChange}
        onOperationIdChange={vi.fn()}
        onServerConfigIdChange={vi.fn()}
        serverConfigId="cfg-1"
      />
    );

    const toggle = await screen.findByTitle(/Pick this value from a live resource list/i);
    fireEvent.click(toggle);

    expect(onArgumentsChange).toHaveBeenCalledWith({
      _locators: { path: { id: { enabled: true } } },
    });
  });

  it('locator_config_inputs_render_when_enabled', async () => {
    vi.mocked(client.getOperation).mockResolvedValueOnce(mockGetPetByIdOp as any);

    render(
      <RestCallerPropertyForm
        specId="petstore"
        operationId="getPetById"
        arguments={{ _locators: { path: { id: { enabled: true } } } }}
        onArgumentsChange={vi.fn()}
        onOperationIdChange={vi.fn()}
        onServerConfigIdChange={vi.fn()}
        serverConfigId="cfg-1"
      />
    );

    expect(await screen.findByPlaceholderText('collection path e.g. pets')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('label field (name)')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('value field (id)')).toBeInTheDocument();
  });

  it('auto_detected_suggestion_shows_hint_and_prefills_on_enable', async () => {
    vi.mocked(client.getOperation).mockResolvedValueOnce(mockGetPetByIdOp as any);
    vi.mocked(client.getLocatorSuggestions).mockResolvedValue([
      { name: 'id', in: 'path', collectionPath: '/pets', valueField: 'id', labelField: 'name', dependsOn: [] },
    ]);
    const onArgumentsChange = vi.fn();

    render(
      <RestCallerPropertyForm
        specId="petstore"
        operationId="getPetById"
        arguments={{}}
        onArgumentsChange={onArgumentsChange}
        onOperationIdChange={vi.fn()}
        onServerConfigIdChange={vi.fn()}
        serverConfigId="cfg-1"
      />
    );

    // Hint surfaces the detected collection endpoint.
    expect(await screen.findByText(/Auto-detected/i)).toBeInTheDocument();

    // Enabling the locator pre-fills config from the suggestion.
    fireEvent.click(screen.getByTitle(/Pick this value from a live resource list/i));
    expect(onArgumentsChange).toHaveBeenCalledWith({
      _locators: {
        path: { id: { enabled: true, path: '/pets', labelField: 'name', valueField: 'id', dependsOn: [] } },
      },
    });
  });

  it('serverConfig_dropdown_shows_options', async () => {
    vi.mocked(client.getOperation).mockResolvedValueOnce(mockGetPetByIdOp as any);

    render(
      <RestCallerPropertyForm
        specId="petstore"
        operationId="getPetById"
        arguments={{}}
        onArgumentsChange={vi.fn()}
        onOperationIdChange={vi.fn()}
        onServerConfigIdChange={vi.fn()}
      />
    );

    const select = await screen.findByLabelText(/Server Config/i);
    const options = select.querySelectorAll('option');
    expect(options).toHaveLength(3); // 'Select server config...' + 2 configs
    expect(options[1].textContent).toContain('Development');
    expect(options[2].textContent).toContain('Production');
  });
});
