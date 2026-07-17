// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { ExternalDeviceLanes } from './ExternalDeviceLanes';

// The lanes only need Handle to render a DOM node carrying its id + a11y props, so stub it.
vi.mock('@xyflow/react', async () => {
  const actual = (await vi.importActual('@xyflow/react')) as Record<string, unknown>;
  return {
    ...actual,
    Handle: ({ id, type, 'aria-label': ariaLabel }: { id?: string; type?: string; 'aria-label'?: string }) => (
      <div data-testid="handle" data-handle-id={id} data-handle-type={type} aria-label={ariaLabel} />
    ),
  };
});

const noGlow = () => ({});
const a11y = (label: string) => ({ 'aria-label': label });

describe('ExternalDeviceLanes', () => {
  it('renders events and incoming actions both as source pins, with prefixed handle ids', () => {
    render(
      <ExternalDeviceLanes
        targetLabel="Site A"
        events={[{ value: 'VehicleRecognised', label: 'Vehicle recognised' }]}
        actions={[{ value: 'StartRecording', label: 'Start recording' }]}
        glowFor={noGlow}
        portA11yProps={a11y}
        displayName="Device Workflow"
      />,
    );

    expect(screen.getByText('Vehicle recognised')).toBeTruthy();
    expect(screen.getByText('Start recording')).toBeTruthy();

    const handles = screen.getAllByTestId('handle');
    const byId = Object.fromEntries(handles.map((h) => [h.getAttribute('data-handle-id'), h]));

    expect(byId['evt:VehicleRecognised']?.getAttribute('data-handle-type')).toBe('source');
    expect(byId['act:StartRecording']?.getAttribute('data-handle-type')).toBe('source');
  });

  it('shows the per-column counts', () => {
    render(
      <ExternalDeviceLanes
        targetLabel="Site A"
        events={[{ value: 'E1', label: 'E1' }, { value: 'E2', label: 'E2' }]}
        actions={[{ value: 'A1', label: 'A1' }]}
        glowFor={noGlow}
        portA11yProps={a11y}
        displayName="dev"
      />,
    );
    expect(screen.getByText('Events')).toBeTruthy();
    expect(screen.getByText('Actions')).toBeTruthy();
    // counts: 2 events, 1 action
    expect(screen.getByText('2')).toBeTruthy();
    expect(screen.getByText('1')).toBeTruthy();
  });

  it('prompts to pick a device when nothing is configured', () => {
    render(
      <ExternalDeviceLanes
        targetLabel=""
        events={[]}
        actions={[]}
        glowFor={noGlow}
        portA11yProps={a11y}
        displayName="dev"
      />,
    );
    expect(screen.getByText(/Pick a device/)).toBeTruthy();
    expect(screen.queryAllByTestId('handle')).toHaveLength(0);
  });

  it('prompts to tick signals once a device is picked but no pins exist', () => {
    render(
      <ExternalDeviceLanes
        targetLabel="Site A"
        events={[]}
        actions={[]}
        glowFor={noGlow}
        portA11yProps={a11y}
        displayName="dev"
      />,
    );
    expect(screen.getByText(/Tick events \/ actions/)).toBeTruthy();
  });
});
