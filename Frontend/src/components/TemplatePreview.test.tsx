import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { TemplatePreview } from './TemplatePreview';
import type { NodeDefinition, EdgeDefinition } from '../types';

const nodes: NodeDefinition[] = [
  { id: { value: 'start-1' }, type: 'start', properties: {} },
  // A residual {{param:…}} token sitting in a field a schema would treat as a number must not break the preview.
  { id: { value: 'poll-1' }, type: 'pollingTrigger', properties: { intervalSeconds: '{{param:interval}}', label: 'Poller' } },
];
const edges: EdgeDefinition[] = [
  { id: 'e1', from: { value: 'start-1' }, output: 'result', to: { value: 'poll-1' }, input: 'in' },
];

describe('TemplatePreview', () => {
  it('renders a node per definition, tolerating a residual token in a typed field', () => {
    render(<TemplatePreview nodes={nodes} edges={edges} />);
    // The start node has no label property, so name + type both read "start" (rendered, not thrown).
    expect(screen.getAllByText('start').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('Poller')).toBeInTheDocument();      // uses the label property
    expect(screen.getByText('pollingTrigger')).toBeInTheDocument();
  });

  it('shows an empty state when there are no nodes', () => {
    render(<TemplatePreview nodes={[]} edges={[]} />);
    expect(screen.getByText(/no nodes to preview/i)).toBeInTheDocument();
  });
});
