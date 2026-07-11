import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { CredentialSlotBinding } from './CredentialSlotBinding';
import type { CredentialSummary, TemplateCredentialSlot } from '../types';

const slots: TemplateCredentialSlot[] = [
  { slot: 'weather-api', displayName: 'Weather API', description: 'Used by the HTTP node', requiredCredentialType: null },
];
const credentials: CredentialSummary[] = [
  { id: 'cred-1', name: 'Prod Key' },
  { id: 'cred-2', name: 'Test Key' },
];

describe('CredentialSlotBinding', () => {
  it('renders nothing when there are no slots', () => {
    const { container } = render(
      <CredentialSlotBinding slots={[]} credentials={credentials} bindings={{}} onChange={() => {}} />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('renders one dropdown per slot with the credentials as options', () => {
    render(<CredentialSlotBinding slots={slots} credentials={credentials} bindings={{}} onChange={() => {}} />);
    expect(screen.getByText('Weather API')).toBeInTheDocument();
    expect(screen.getByText('Used by the HTTP node')).toBeInTheDocument();
    expect(screen.getByRole('option', { name: /Prod Key/ })).toBeInTheDocument();
  });

  it('reports a binding when a credential is selected', () => {
    const onChange = vi.fn();
    render(<CredentialSlotBinding slots={slots} credentials={credentials} bindings={{}} onChange={onChange} />);
    fireEvent.change(screen.getByLabelText('Bind credential for slot weather-api'), { target: { value: 'cred-2' } });
    expect(onChange).toHaveBeenCalledWith({ 'weather-api': 'cred-2' });
  });

  it('clears a binding when set back to unbound', () => {
    const onChange = vi.fn();
    render(
      <CredentialSlotBinding slots={slots} credentials={credentials} bindings={{ 'weather-api': 'cred-1' }} onChange={onChange} />,
    );
    fireEvent.change(screen.getByLabelText('Bind credential for slot weather-api'), { target: { value: '' } });
    expect(onChange).toHaveBeenCalledWith({});
  });
});
