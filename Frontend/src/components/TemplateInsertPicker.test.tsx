import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { TemplateInsertPicker } from './TemplateInsertPicker';
import type { GalleryTemplate, TemplatePayloadResponse } from '../types';

vi.mock('../utils/api', async () => {
  const actual = await vi.importActual<typeof import('../utils/api')>('../utils/api');
  return {
    ...actual,
    api: {
      listGalleryTemplates: vi.fn(),
      listLibraryTemplates: vi.fn(),
      getGalleryTemplatePayload: vi.fn(),
      getLibraryTemplatePayload: vi.fn(),
      getTemplatePayload: vi.fn(),
    },
  };
});

import { api } from '../utils/api';

const gallery: GalleryTemplate[] = [
  {
    templateId: 'tpl_starter-hello-world',
    manifest: {
      templateId: 'tpl_starter-hello-world', templateVersion: '1.0.0', schemaVersion: 1, name: 'Hello World',
      author: 'KnotGarden', description: 'A minimal starter.', tags: [], category: 'starter',
      minEngineVersion: null, createdAtUtc: '', sourceWorkflowName: 'Hello World', workflowChecksum: '', credentialSlots: [], parameters: [],
    },
  },
];

const payload: TemplatePayloadResponse = {
  manifest: gallery[0].manifest,
  credentialSlots: [],
  compatibility: { supported: true, warnings: [] },
  nodes: [{ id: { value: 'start-1' }, type: 'start', properties: {} }],
  edges: [],
};

describe('TemplateInsertPicker', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(api.listGalleryTemplates).mockResolvedValue(gallery);
    vi.mocked(api.listLibraryTemplates).mockResolvedValue([]);
    vi.mocked(api.getGalleryTemplatePayload).mockResolvedValue(payload);
  });

  it('fetches the payload and hands it to onInsert, then closes', async () => {
    const onInsert = vi.fn();
    const onClose = vi.fn();
    render(<TemplateInsertPicker onInsert={onInsert} onClose={onClose} />);

    await screen.findByText('Hello World');
    fireEvent.click(screen.getByRole('button', { name: 'Insert Hello World' }));

    await waitFor(() => expect(api.getGalleryTemplatePayload).toHaveBeenCalledWith('tpl_starter-hello-world', {}));
    expect(onInsert).toHaveBeenCalledWith(payload);
    expect(onClose).toHaveBeenCalled();
  });

  it('lists saved library templates as a source and inserts from the library', async () => {
    const libraryItem = { templateId: 'tpl_saved', manifest: { ...gallery[0].manifest, name: 'My Saved Flow' } };
    vi.mocked(api.listLibraryTemplates).mockResolvedValue([libraryItem]);
    vi.mocked(api.getLibraryTemplatePayload).mockResolvedValue(payload);
    const onInsert = vi.fn();
    const onClose = vi.fn();
    render(<TemplateInsertPicker onInsert={onInsert} onClose={onClose} />);

    await screen.findByText('From your library');
    fireEvent.click(screen.getByRole('button', { name: 'Insert My Saved Flow' }));

    await waitFor(() => expect(api.getLibraryTemplatePayload).toHaveBeenCalledWith('tpl_saved', {}));
    expect(onInsert).toHaveBeenCalledWith(payload);
  });
});
