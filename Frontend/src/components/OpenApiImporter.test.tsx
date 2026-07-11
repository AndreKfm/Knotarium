import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { OpenApiImporter } from './OpenApiImporter';

vi.mock('../utils/openApiClient', () => ({
  importSpec: vi.fn(),
  importSpecFromUrl: vi.fn(),
}));

import * as client from '../utils/openApiClient';

const mockSpec = {
  id: 'spec-abc',
  title: 'Petstore API',
  apiVersion: 'OpenAPI 3.0',
  latestVersionNumber: 1,
  importedAtUtc: '2026-06-07T00:00:00Z',
};

describe('OpenApiImporter', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders_textarea_and_upload_button', () => {
    render(<OpenApiImporter onImported={() => {}} />);
    expect(screen.getByLabelText('OpenAPI content')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /import/i })).toBeInTheDocument();
  });

  it('submit_with_empty_content_shows_error', async () => {
    render(<OpenApiImporter onImported={() => {}} />);
    fireEvent.click(screen.getByRole('button', { name: /import/i }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/content/i);
  });

  it('submit_valid_yaml_calls_importSpec', async () => {
    vi.mocked(client.importSpec).mockResolvedValueOnce(mockSpec);
    render(<OpenApiImporter onImported={() => {}} />);
    fireEvent.change(screen.getByLabelText('OpenAPI content'), { target: { value: 'openapi: "3.0.0"' } });
    fireEvent.click(screen.getByRole('button', { name: /import/i }));
    await waitFor(() => expect(vi.mocked(client.importSpec)).toHaveBeenCalledWith('openapi: "3.0.0"', ''));
  });

  it('successful_import_calls_onImported', async () => {
    vi.mocked(client.importSpec).mockResolvedValueOnce(mockSpec);
    const onImported = vi.fn();
    render(<OpenApiImporter onImported={onImported} />);
    fireEvent.change(screen.getByLabelText('OpenAPI content'), { target: { value: 'openapi: "3.0.0"' } });
    fireEvent.click(screen.getByRole('button', { name: /import/i }));
    await waitFor(() => expect(onImported).toHaveBeenCalledWith(mockSpec));
  });

  it('url_tab_imports_from_url', async () => {
    vi.mocked(client.importSpecFromUrl).mockResolvedValueOnce(mockSpec);
    const onImported = vi.fn();
    render(<OpenApiImporter onImported={onImported} />);

    fireEvent.click(screen.getByRole('button', { name: /from url/i }));
    fireEvent.change(screen.getByLabelText('OpenAPI spec URL'), {
      target: { value: 'https://petstore3.swagger.io/api/v3/openapi.json' },
    });
    fireEvent.click(screen.getByRole('button', { name: /import spec/i }));

    await waitFor(() => expect(vi.mocked(client.importSpecFromUrl)).toHaveBeenCalledWith(
      'https://petstore3.swagger.io/api/v3/openapi.json', ''));
    await waitFor(() => expect(onImported).toHaveBeenCalledWith(mockSpec));
  });

  it('url_tab_empty_shows_error', async () => {
    render(<OpenApiImporter onImported={() => {}} />);
    fireEvent.click(screen.getByRole('button', { name: /from url/i }));
    fireEvent.click(screen.getByRole('button', { name: /import spec/i }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/url/i);
  });

  it('api_error_shows_error_message', async () => {
    vi.mocked(client.importSpec).mockRejectedValueOnce(new Error('External $ref not supported'));
    render(<OpenApiImporter onImported={() => {}} />);
    fireEvent.change(screen.getByLabelText('OpenAPI content'), { target: { value: 'openapi: "3.0.0"' } });
    fireEvent.click(screen.getByRole('button', { name: /import/i }));
    expect(await screen.findByRole('alert')).toHaveTextContent('External $ref not supported');
  });
});
