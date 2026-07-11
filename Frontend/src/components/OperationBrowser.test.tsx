import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { OperationBrowser } from './OperationBrowser';

vi.mock('../utils/openApiClient', () => ({
  getSpecDetail: vi.fn(),
}));

import * as client from '../utils/openApiClient';

const mockDetail = {
  id: 'petstore',
  title: 'Petstore',
  groups: [
    {
      tag: 'pets',
      operations: [
        {
          operationId: 'listPets',
          method: 'GET',
          pathTemplate: '/pets',
          summary: 'List all pets',
          tags: ['pets'],
          parameters: [],
        },
        {
          operationId: 'createPet',
          method: 'POST',
          pathTemplate: '/pets/new',
          summary: 'Create a pet',
          tags: ['pets'],
          parameters: [],
        },
      ],
    },
    {
      tag: 'store',
      operations: [
        {
          operationId: 'updateInventory',
          method: 'PUT',
          pathTemplate: '/store/inventory',
          summary: 'Update inventory',
          tags: ['store'],
          parameters: [],
        },
      ],
    },
  ],
  schemas: [],
};

describe('OperationBrowser', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders_loading_state', () => {
    vi.mocked(client.getSpecDetail).mockReturnValueOnce(new Promise(() => {}));
    render(<OperationBrowser specId="petstore" />);
    expect(screen.getByText(/loading/i)).toBeInTheDocument();
  });

  it('renders_groups_from_api', async () => {
    vi.mocked(client.getSpecDetail).mockResolvedValueOnce(mockDetail);
    render(<OperationBrowser specId="petstore" />);
    expect(await screen.findByText('pets')).toBeInTheDocument();
    expect(await screen.findByText('store')).toBeInTheDocument();
  });

  it('renders_operation_method_and_path', async () => {
    vi.mocked(client.getSpecDetail).mockResolvedValueOnce(mockDetail);
    render(<OperationBrowser specId="petstore" />);
    expect(await screen.findByText('GET')).toBeInTheDocument();
    expect(await screen.findByText('/pets')).toBeInTheDocument();
  });

  it('group_collapse_toggle_works', async () => {
    vi.mocked(client.getSpecDetail).mockResolvedValueOnce(mockDetail);
    render(<OperationBrowser specId="petstore" />);

    const petsHeader = await screen.findByText('pets');
    expect(screen.getByText('/pets')).toBeInTheDocument();

    // Collapse
    fireEvent.click(petsHeader.closest('[role="button"]') ?? petsHeader);
    await waitFor(() => expect(screen.queryByText('/pets')).not.toBeInTheDocument());

    // Expand again
    fireEvent.click(petsHeader.closest('[role="button"]') ?? petsHeader);
    await waitFor(() => expect(screen.getByText('/pets')).toBeInTheDocument());
  });

  it('api_error_shows_error_state', async () => {
    vi.mocked(client.getSpecDetail).mockRejectedValueOnce(new Error('Network error'));
    render(<OperationBrowser specId="petstore" />);
    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
