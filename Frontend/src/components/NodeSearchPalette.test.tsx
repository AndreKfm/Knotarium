import { render, screen, fireEvent } from '@testing-library/react';
import { vi, describe, it, expect } from 'vitest';
import { NodeSearchPalette } from './NodeSearchPalette';
import type { SearchableNode } from '../node-editor/nodeSearch';

const nodes: SearchableNode[] = [
  { id: 'n1', type: 'http', data: { displayName: 'HTTP Request' } },
  { id: 'n2', type: 'condition', data: { displayName: 'Check Status' } },
  { id: 'n3', type: 'http', data: { displayName: 'Health Check' } },
  { id: 'n4', type: 'subflow', data: { displayName: 'Subflow', subflowName: 'Send Invoice' } },
];

function setup() {
  const onClose = vi.fn();
  const onPick = vi.fn();
  render(<NodeSearchPalette nodes={nodes} onClose={onClose} onPick={onPick} />);
  const input = screen.getByLabelText('Search nodes by name') as HTMLInputElement;
  return { onClose, onPick, input };
}

describe('NodeSearchPalette', () => {
  it('lists every node when the query is empty', () => {
    setup();
    expect(screen.getByText('HTTP Request')).toBeTruthy();
    expect(screen.getByText('Check Status')).toBeTruthy();
    expect(screen.getByText('Send Invoice')).toBeTruthy();
  });

  it('filters the list as the user types', () => {
    const { input } = setup();
    fireEvent.change(input, { target: { value: 'check' } });
    expect(screen.getByText('Check Status')).toBeTruthy();
    expect(screen.getByText('Health Check')).toBeTruthy();
    expect(screen.queryByText('HTTP Request')).toBeNull();
  });

  it('shows an empty state when nothing matches', () => {
    const { input } = setup();
    fireEvent.change(input, { target: { value: 'zzzzz' } });
    expect(screen.getByText('No matching nodes')).toBeTruthy();
  });

  it('picks the highlighted result on Enter and closes', () => {
    const { input, onPick, onClose } = setup();
    fireEvent.change(input, { target: { value: 'http' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onPick).toHaveBeenCalledTimes(1);
    expect(onPick.mock.calls[0][0].id).toBe('n1'); // best match ranked first
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('moves the highlight with ArrowDown before committing', () => {
    const { input, onPick } = setup();
    fireEvent.change(input, { target: { value: 'check' } });
    // results: ['Check Status' (n2), 'Health Check' (n3)] — arrow down to the 2nd
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onPick.mock.calls[0][0].id).toBe('n3');
  });

  it('wraps the highlight with ArrowUp from the top', () => {
    const { input, onPick } = setup();
    fireEvent.change(input, { target: { value: 'check' } });
    fireEvent.keyDown(input, { key: 'ArrowUp' }); // wrap to last
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onPick.mock.calls[0][0].id).toBe('n3');
  });

  it('picks a result on click', () => {
    const { onPick, onClose } = setup();
    fireEvent.mouseDown(screen.getByText('Send Invoice'));
    expect(onPick.mock.calls[0][0].id).toBe('n4');
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('closes on Escape without picking', () => {
    const { input, onPick, onClose } = setup();
    fireEvent.keyDown(input, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(onPick).not.toHaveBeenCalled();
  });

  it('closes when the backdrop is clicked', () => {
    const { onClose } = setup();
    fireEvent.mouseDown(screen.getByRole('dialog'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does not pick or crash when committing with no results', () => {
    const { input, onPick, onClose } = setup();
    fireEvent.change(input, { target: { value: 'zzzzz' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onPick).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });
});
