import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { ComponentProps } from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { ReactFlowProvider } from '@xyflow/react';
import { GenericCustomNode } from './CustomNodes';

// Capture setNodes so we can assert the commit wiring without a live RF store
// (GenericCustomNode reads its label off props, not the store).
const { setNodesSpy } = vi.hoisted(() => ({ setNodesSpy: vi.fn() }));

vi.mock('@xyflow/react', async () => {
  const actual = (await vi.importActual('@xyflow/react')) as Record<string, unknown>;
  return {
    ...actual,
    Handle: ({ id }: { id?: string }) => <div data-testid="rf-handle" data-id={id} />,
    useReactFlow: () => ({ setNodes: setNodesSpy, getNode: () => undefined }),
  };
});

const baseProps = {
  id: 'node-1',
  type: 'log',
  data: { displayName: 'My Logger', properties: { message: 'hi' } },
  selected: false,
  zIndex: 1,
  isConnectable: true,
  positionAbsoluteX: 0,
  positionAbsoluteY: 0,
  dragging: false,
  draggable: true,
  selectable: true,
  deletable: true,
};

type NodeProps = ComponentProps<typeof GenericCustomNode>;

function renderNode(props: NodeProps = baseProps as NodeProps) {
  return render(
    <ReactFlowProvider>
      <GenericCustomNode {...props} />
    </ReactFlowProvider>,
  );
}

describe('GenericCustomNode inline rename', () => {
  beforeEach(() => setNodesSpy.mockClear());

  it('shows the label with a rename hint by default (no input)', () => {
    renderNode();
    const label = screen.getByText('My Logger');
    expect(label).toHaveAttribute('title', 'Double-click to rename');
    expect(screen.queryByRole('textbox', { name: 'Rename node' })).toBeNull();
  });

  it('double-clicking the label opens an input prefilled with the current name', () => {
    renderNode();
    fireEvent.doubleClick(screen.getByText('My Logger'));
    const input = screen.getByRole('textbox', { name: 'Rename node' }) as HTMLInputElement;
    expect(input.value).toBe('My Logger');
  });

  it('Enter commits the new name via setNodes (updater renames the node)', () => {
    renderNode();
    fireEvent.doubleClick(screen.getByText('My Logger'));
    const input = screen.getByRole('textbox', { name: 'Rename node' });
    fireEvent.change(input, { target: { value: '  Renamed Logger  ' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(setNodesSpy).toHaveBeenCalledTimes(1);
    const updater = setNodesSpy.mock.calls[0][0] as (n: unknown[]) => unknown[];
    const result = updater([{ id: 'node-1', type: 'log', data: { displayName: 'My Logger' } }]) as Array<{
      data: { displayName: string };
    }>;
    expect(result[0].data.displayName).toBe('Renamed Logger');
    // Input closed after commit.
    expect(screen.queryByRole('textbox', { name: 'Rename node' })).toBeNull();
  });

  it('Escape cancels without committing', () => {
    renderNode();
    fireEvent.doubleClick(screen.getByText('My Logger'));
    const input = screen.getByRole('textbox', { name: 'Rename node' });
    fireEvent.change(input, { target: { value: 'Discarded' } });
    fireEvent.keyDown(input, { key: 'Escape' });

    expect(setNodesSpy).not.toHaveBeenCalled();
    expect(screen.getByText('My Logger')).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'Rename node' })).toBeNull();
  });

  it('does not offer rename for subflow cards', () => {
    renderNode({
      ...baseProps,
      type: 'subflow',
      data: { displayName: 'Sub1', subflowName: 'Sub1', properties: {} },
    });
    const label = screen.getByText('Sub1');
    expect(label).not.toHaveAttribute('title', 'Double-click to rename');
    fireEvent.doubleClick(label);
    expect(screen.queryByRole('textbox', { name: 'Rename node' })).toBeNull();
  });
});
