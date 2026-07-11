import { fireEvent, render, screen, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { SidebarPalette } from '../components/SidebarPalette';
import { canvasPaletteStorageKey, useCanvasStore } from '../stores/useCanvasStore';
import type { GalleryTemplate, NodePackageSummary } from '../types';

vi.mock('../utils/api', () => ({
  api: {
    listGalleryTemplates: vi.fn().mockResolvedValue([]),
    listLibraryTemplates: vi.fn().mockResolvedValue([]),
  },
}));
import { api } from '../utils/api';

function galleryTemplate(name: string, params: GalleryTemplate['manifest']['parameters'] = []): GalleryTemplate {
  return {
    templateId: `tpl_${name.toLowerCase().replace(/\s+/g, '-')}`,
    manifest: {
      templateId: `tpl_${name}`, templateVersion: '1.0.0', schemaVersion: 1, name,
      author: 'me', description: 'A test template', tags: [], category: 'starter',
      minEngineVersion: null, createdAtUtc: '', sourceWorkflowName: name, workflowChecksum: '',
      credentialSlots: [], parameters: params,
    },
  };
}

function createPackage(
  id: string,
  displayName: string,
  category: string,
  description?: string,
): NodePackageSummary {
  return {
    id,
    displayName,
    category,
    versions: [
      {
        id: `${id}-version`,
        nodePackageId: id,
        version: '1.0.0',
        manifestJson: JSON.stringify({
          id,
          displayName,
          category,
          description,
        }),
        source: 'test',
        capabilities: [],
        createdAt: '2026-05-31T00:00:00Z',
      },
    ],
  };
}

describe('SidebarPalette', () => {
  const availableNodes: NodePackageSummary[] = [
    createPackage('webhookTrigger', 'Webhook Trigger', 'Triggers'),
    createPackage('condition', 'Condition', 'Control'),
    createPackage('delay', 'Delay', 'Control'),
    createPackage('httpRequest', 'HTTP Request', 'Integrations', 'Call an external API'),
    createPackage('transform', 'Transform', 'Data'),
    createPackage('log', 'Log', 'Utility'),
  ];

  beforeEach(() => {
    localStorage.clear();
    useCanvasStore.getState().resetPaletteState();
    useCanvasStore.persist.clearStorage();
    vi.mocked(api.listGalleryTemplates).mockResolvedValue([]);
    vi.mocked(api.listLibraryTemplates).mockResolvedValue([]);
  });

  it('lists templates and drags one with a template payload', async () => {
    vi.mocked(api.listGalleryTemplates).mockResolvedValue([galleryTemplate('Hello World')]);
    vi.mocked(api.listLibraryTemplates).mockResolvedValue([]);

    render(
      <SidebarPalette availableNodes={availableNodes} onAddNode={vi.fn()} onDragStart={vi.fn()} />,
    );

    const item = await screen.findByTestId('palette-template-tpl_hello-world');
    const setData = vi.fn();
    fireEvent.dragStart(item, { dataTransfer: { setData, effectAllowed: '' } });

    expect(setData).toHaveBeenCalledWith(
      'application/knotgarden-template',
      JSON.stringify({ source: 'gallery', templateId: 'tpl_hello-world', needsConfig: false }),
    );
  });

  it('marks a template that has a required parameter as needing configuration', async () => {
    vi.mocked(api.listGalleryTemplates).mockResolvedValue([
      galleryTemplate('Needs Config', [{ key: 'token', label: 'Token', description: null, type: 'string', options: null, default: null, required: true }]),
    ]);

    render(
      <SidebarPalette availableNodes={availableNodes} onAddNode={vi.fn()} onDragStart={vi.fn()} />,
    );

    const item = await screen.findByTestId('palette-template-tpl_needs-config');
    const setData = vi.fn();
    fireEvent.dragStart(item, { dataTransfer: { setData, effectAllowed: '' } });

    expect(setData).toHaveBeenCalledWith(
      'application/knotgarden-template',
      expect.stringContaining('"needsConfig":true'),
    );
  });

  it('renders all palette categories as collapsible sections', () => {
    render(
      <SidebarPalette
        availableNodes={availableNodes}
        onAddNode={vi.fn()}
        onDragStart={vi.fn()}
      />,
    );

    const triggerHeader = within(screen.getByTestId('palette-category-trigger')).getByRole('button', { name: /^trigger/i });
    const logicHeader = within(screen.getByTestId('palette-category-logic')).getByRole('button', { name: /^logic/i });
    const dataHeader = within(screen.getByTestId('palette-category-data')).getByRole('button', { name: /^data/i });
    const networkHeader = within(screen.getByTestId('palette-category-network')).getByRole('button', { name: /^network/i });
    const utilityHeader = within(screen.getByTestId('palette-category-utility')).getByRole('button', { name: /^utility/i });

    expect(triggerHeader).toHaveAttribute('aria-expanded', 'true');
    expect(logicHeader).toHaveAttribute('aria-expanded', 'true');
    expect(dataHeader).toHaveAttribute('aria-expanded', 'true');
    expect(networkHeader).toHaveAttribute('aria-expanded', 'true');
    expect(utilityHeader).toHaveAttribute('aria-expanded', 'true');

    fireEvent.click(logicHeader);
    expect(logicHeader).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByTestId('palette-node-condition')).not.toBeInTheDocument();
  });

  it('renders the recent and pinned section at the top of the palette', () => {
    localStorage.setItem(canvasPaletteStorageKey, JSON.stringify({
      state: {
        pinnedNodeIds: ['log'],
        recentNodeIds: ['httpRequest', 'condition'],
      },
      version: 0,
    }));
    useCanvasStore.persist.rehydrate();

    render(
      <SidebarPalette
        availableNodes={availableNodes}
        onAddNode={vi.fn()}
        onDragStart={vi.fn()}
      />,
    );

    const recentPinnedSection = screen.getByTestId('palette-category-recent-pinned');
    const recentPinnedHeader = within(recentPinnedSection).getByRole('button', { name: /^recent \/ pinned/i });
    expect(recentPinnedHeader).toHaveAttribute('aria-expanded', 'true');

    const recentPinnedItems = within(recentPinnedSection).getAllByTestId(/palette-node-/i);
    expect(recentPinnedItems.map((item) => item.getAttribute('data-testid'))).toEqual([
      'palette-node-log',
      'palette-node-httpRequest',
      'palette-node-condition',
    ]);
  });

  it('filters node items from the pinned search input', () => {
    render(
      <SidebarPalette
        availableNodes={availableNodes}
        onAddNode={vi.fn()}
        onDragStart={vi.fn()}
      />,
    );

    fireEvent.change(screen.getByLabelText('Search nodes'), { target: { value: 'external api' } });

    expect(screen.getByTestId('palette-node-httpRequest')).toBeInTheDocument();
    expect(screen.queryByTestId('palette-node-condition')).not.toBeInTheDocument();
    expect(screen.queryByTestId('palette-node-log')).not.toBeInTheDocument();
  });

  it('sorts nodes alphabetically inside each category', () => {
    render(
      <SidebarPalette
        availableNodes={availableNodes}
        onAddNode={vi.fn()}
        onDragStart={vi.fn()}
      />,
    );

    const logicCategory = screen.getByTestId('palette-category-logic');
    const logicButtons = within(logicCategory).getAllByTestId(/palette-node-/i);

    expect(logicButtons.map(button => button.textContent)).toEqual([
      expect.stringContaining('Condition'),
      expect.stringContaining('Delay'),
    ]);
  });

  it('routes secondary (escape-hatch) nodes into a collapsed Advanced section', () => {
    const secondaryNode: NodePackageSummary = {
      id: 'fireAction',
      displayName: 'Fire Action',
      category: 'Device Workflow',
      versions: [{
        id: 'fireAction-version', nodePackageId: 'fireAction', version: '1.0.0',
        manifestJson: JSON.stringify({ id: 'fireAction', displayName: 'Fire Action', category: 'Device Workflow', secondary: true }),
        source: 'test', capabilities: [], createdAt: '2026-05-31T00:00:00Z',
      }],
    };
    const primaryDevice = createPackage('externalDevice', 'Device Workflow', 'Device Workflow');

    render(
      <SidebarPalette
        availableNodes={[...availableNodes, secondaryNode, primaryDevice]}
        onAddNode={vi.fn()}
        onDragStart={vi.fn()}
      />,
    );

    // The primary device block sits in a normal (expanded) category; the escape hatch does not.
    expect(screen.getByTestId('palette-node-externalDevice')).toBeInTheDocument();
    expect(screen.queryByTestId('palette-node-fireAction')).not.toBeInTheDocument();

    const advanced = screen.getByTestId('palette-category-advanced');
    const advancedHeader = within(advanced).getByRole('button', { name: /^advanced/i });
    expect(advancedHeader).toHaveAttribute('aria-expanded', 'false');

    fireEvent.click(advancedHeader);
    expect(within(advanced).getByTestId('palette-node-fireAction')).toBeInTheDocument();
  });

  it('toggles a node pin directly from the palette item action', () => {
    render(
      <SidebarPalette
        availableNodes={availableNodes}
        onAddNode={vi.fn()}
        onDragStart={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByTestId('palette-pin-log'));

    expect(useCanvasStore.getState().pinnedNodeIds).toEqual(['log']);
    expect(screen.getAllByLabelText('Unpin Log')).toHaveLength(2);
  });
});