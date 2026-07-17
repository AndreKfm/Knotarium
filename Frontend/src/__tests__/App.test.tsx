// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App from '../App';
import { AuthProvider } from '../components/auth/AuthContext';

vi.mock('../components/Dashboard', () => ({
  Dashboard: ({ onEditWorkflow }: { onEditWorkflow: (id: string) => void }) => (
    <div>
      <div>Dashboard View</div>
      <button onClick={() => onEditWorkflow('missing-workflow-id')}>Edit Missing Workflow</button>
    </div>
  ),
}));

vi.mock('../components/Canvas', () => ({
  Canvas: ({ workflowId, onTriggered, onWorkflowLoadFailed }: { workflowId: string | null; onTriggered?: (id: string) => void; onWorkflowLoadFailed?: (workflowId: string) => void }) => (
    <div>
      <div>Canvas View</div>
      <div>Workflow: {workflowId ?? 'none'}</div>
      <button onClick={() => onTriggered?.('execution-456')}>Run Workflow</button>
      <button onClick={() => onWorkflowLoadFailed?.('missing-workflow-id')}>Simulate Missing Workflow</button>
    </div>
  ),
}));

vi.mock('../components/ExecutionDetail/index', () => ({
  ExecutionDetail: ({ onBack }: { onBack: () => void }) => (
    <div>
      <div>Execution Detail View</div>
      <button onClick={onBack}>Back From Execution</button>
    </div>
  ),
}));

vi.mock('../node-editor/NodeEditorShell', () => ({
  NodeEditorShell: () => <div>Node Editor View</div>,
}));

describe('App', () => {
  afterEach(() => {
    vi.clearAllMocks();
    window.sessionStorage.clear();
  });

  it('returns to the dashboard when the selected workflow can no longer be loaded', () => {
    render(<AuthProvider><App /></AuthProvider>);

    fireEvent.click(screen.getByRole('button', { name: 'Edit Missing Workflow' }));
    expect(screen.getByText('Canvas View')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Simulate Missing Workflow' }));

    expect(screen.getByText('Dashboard View')).toBeInTheDocument();
    expect(screen.queryByText('Canvas View')).not.toBeInTheDocument();
  });

  it('restores the execution view from session storage after a remount', () => {
    window.sessionStorage.setItem('knotarium-navigation-state', JSON.stringify({
      currentView: 'execution',
      selectedWorkflowId: null,
      selectedExecutionId: 'execution-123',
      lastNonExecutionView: 'dashboard',
    }));

    render(<AuthProvider><App /></AuthProvider>);

    expect(screen.getByText('Execution Detail View')).toBeInTheDocument();
    expect(screen.queryByText('Dashboard View')).not.toBeInTheDocument();
  });

  it('returns to the last editor view after backing out of execution detail', () => {
    render(<AuthProvider><App /></AuthProvider>);

    fireEvent.click(screen.getByRole('button', { name: 'Edit Missing Workflow' }));

    expect(screen.getByText('Canvas View')).toBeInTheDocument();
    expect(screen.getByText('Workflow: missing-workflow-id')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Run Workflow' }));

    // Running no longer navigates away — the editor stays put and a toast offers to open the run.
    expect(screen.getByText('Canvas View')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'View execution' }));

    expect(screen.getByText('Execution Detail View')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Back From Execution' }));

    expect(screen.getByText('Canvas View')).toBeInTheDocument();
    expect(screen.getByText('Workflow: missing-workflow-id')).toBeInTheDocument();
    expect(screen.queryByText('Execution Detail View')).not.toBeInTheDocument();
  });
});