// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { GenericCustomNode } from '../components/CustomNodes';
import { ReactFlowProvider } from '@xyflow/react';

// Minimal mock for ReactFlow elements
vi.mock('@xyflow/react', async () => {
  const actual = await vi.importActual('@xyflow/react') as Record<string, unknown>;
  return {
    ...actual,
    Handle: ({ id, position, type }: { id?: string; position?: string; type?: string }) => (
      <div data-testid="rf-handle" data-id={id} data-position={position} data-type={type}>
        Handle
      </div>
    ),
  };
});

describe('GenericCustomNode', () => {
  const defaultProps = {
    id: 'test-node-1',
    type: 'log',
    data: {
      properties: {
        label: 'Logger Node',
        message: 'Info log triggered'
      }
    },
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

  it('renders node title and properties summary successfully', () => {
    render(
      <ReactFlowProvider>
        <GenericCustomNode {...defaultProps} />
      </ReactFlowProvider>
    );

    // Assert title label
    expect(screen.getByText('Logger Node')).toBeInTheDocument();
    // Assert summary message
    expect(screen.getByText('"Info log triggered"')).toBeInTheDocument();

    // Verify presence of input and output handles (target & source)
    const handles = screen.getAllByTestId('rf-handle');
    expect(handles).toHaveLength(2); // One input, one output
  });

  it('renders specialized handles for condition nodes', () => {
    const conditionProps = {
      ...defaultProps,
      type: 'condition',
      data: {
        properties: {
          label: 'Verify Age',
          left: 'age',
          operator: 'GreaterThan',
          right: '18'
        }
      }
    };

    render(
      <ReactFlowProvider>
        <GenericCustomNode {...conditionProps} />
      </ReactFlowProvider>
    );

    expect(screen.getByText('Verify Age')).toBeInTheDocument();
    expect(screen.getByText('age GreaterThan 18')).toBeInTheDocument();

    const handles = screen.getAllByTestId('rf-handle');
    // For condition nodes: 1 input target, 2 output sources (true & false handles)
    expect(handles).toHaveLength(3);

    // Verify true/false handles are correctly labeled
    const trueHandle = handles.find((h) => h.getAttribute('data-id') === 'true');
    const falseHandle = handles.find((h) => h.getAttribute('data-id') === 'false');
    expect(trueHandle).toBeDefined();
    expect(falseHandle).toBeDefined();
  });
});
