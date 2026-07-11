import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { TimeTravelInspector, type InspectorStep } from '../components/ExecutionDetail/TimeTravelInspector';
import type { NodeState } from '../types';

const steps: InspectorStep[] = [
  { key: 'start', nodeId: 'start', title: 'Start', status: 'Completed', durationLabel: '0ms' },
  { key: 'reader', nodeId: 'reader', title: 'Reader', status: 'Completed', durationLabel: '12ms' },
];

const nodeStates: NodeState[] = [
  {
    id: 'ns-start', executionInstanceId: 'e1', nodeId: { value: 'start' }, status: 'Completed',
    inputs: {}, outputs: { result: 'go' }, executionCount: 1, variablesBefore: '{}',
  },
  {
    id: 'ns-reader', executionInstanceId: 'e1', nodeId: { value: 'reader' }, status: 'Completed',
    inputs: { in: 'go' }, outputs: { result: 'done' }, executionCount: 1, variablesBefore: '{"x":"hello"}',
  },
];

describe('TimeTravelInspector', () => {
  it('renders the current step and its captured state', () => {
    render(
      <TimeTravelInspector steps={steps} nodeStates={nodeStates} index={1} onIndexChange={vi.fn()} onClose={vi.fn()} />,
    );

    expect(screen.getByText('Step 2 / 2')).toBeInTheDocument();
    expect(screen.getByText('Reader')).toBeInTheDocument();
    // Variables-at-this-step shows the snapshot captured when the node started.
    expect(screen.getByText('x')).toBeInTheDocument();
    expect(screen.getByText('hello')).toBeInTheDocument();
    // Inputs + outputs from the node state.
    expect(screen.getByText('in')).toBeInTheDocument();
    expect(screen.getByText('done')).toBeInTheDocument();
  });

  it('steps backward and forward via the controls', () => {
    const onIndexChange = vi.fn();
    render(
      <TimeTravelInspector steps={steps} nodeStates={nodeStates} index={1} onIndexChange={onIndexChange} onClose={vi.fn()} />,
    );

    fireEvent.click(screen.getByLabelText('Previous step'));
    expect(onIndexChange).toHaveBeenCalledWith(0);
  });

  it('jumps to a step when its scrubber tick is clicked', () => {
    const onIndexChange = vi.fn();
    render(
      <TimeTravelInspector steps={steps} nodeStates={nodeStates} index={1} onIndexChange={onIndexChange} onClose={vi.fn()} />,
    );

    fireEvent.click(screen.getByLabelText('Go to step 1: Start'));
    expect(onIndexChange).toHaveBeenCalledWith(0);
  });

  it('keeps a fixed panel height so controls do not move between steps', () => {
    const { rerender } = render(
      <TimeTravelInspector steps={steps} nodeStates={nodeStates} index={0} onIndexChange={vi.fn()} onClose={vi.fn()} />,
    );

    const panel = screen.getByTestId('time-travel-inspector');
    expect(panel.style.height).toBe('248px');

    // A different step with more/less state must not change the panel height.
    rerender(
      <TimeTravelInspector steps={steps} nodeStates={nodeStates} index={1} onIndexChange={vi.fn()} onClose={vi.fn()} />,
    );
    expect(panel.style.height).toBe('248px');
  });

  it('disables next on the last step and closes', () => {
    const onClose = vi.fn();
    render(
      <TimeTravelInspector steps={steps} nodeStates={nodeStates} index={1} onIndexChange={vi.fn()} onClose={onClose} />,
    );

    expect(screen.getByLabelText('Next step')).toBeDisabled();

    fireEvent.click(screen.getByLabelText('Close inspector'));
    expect(onClose).toHaveBeenCalled();
  });
});
