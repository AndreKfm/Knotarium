import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { TemplateParameterForm, defaultParamValues, paramsSatisfied } from './TemplateParameterForm';
import type { TemplateParameter } from '../types';

const P = (over: Partial<TemplateParameter>): TemplateParameter => ({
  key: 'k', label: 'K', description: null, type: 'string', options: null, default: null, required: true, ...over,
});

describe('parameter helpers', () => {
  it('defaultParamValues prefills only declared defaults', () => {
    const values = defaultParamValues([P({ key: 'a', default: 'x' }), P({ key: 'b', required: false, default: '' })]);
    expect(values).toEqual({ a: 'x' });
  });

  it('paramsSatisfied gates on required values (default counts)', () => {
    const params = [P({ key: 'req', required: true }), P({ key: 'opt', required: false, default: 'd' })];
    expect(paramsSatisfied(params, {})).toBe(false);          // req missing
    expect(paramsSatisfied(params, { req: 'v' })).toBe(true);  // req filled, opt has default
  });
});

describe('TemplateParameterForm', () => {
  it('renders a typed input per parameter and reports edits', () => {
    const onChange = vi.fn();
    render(
      <TemplateParameterForm
        parameters={[
          P({ key: 'channel', label: 'Channel', type: 'string' }),
          P({ key: 'mode', label: 'Mode', type: 'enum', options: ['fast', 'slow'], required: false, default: 'fast' }),
        ]}
        values={{ mode: 'fast' }}
        onChange={onChange}
      />,
    );

    fireEvent.change(screen.getByLabelText('Value for Channel'), { target: { value: '#alerts' } });
    expect(onChange).toHaveBeenCalledWith({ mode: 'fast', channel: '#alerts' });

    // Enum renders its declared options.
    expect(screen.getByRole('option', { name: 'slow' })).toBeInTheDocument();
  });

  it('flags a required-but-empty parameter', () => {
    render(<TemplateParameterForm parameters={[P({ key: 'token', label: 'Token', required: true })]} values={{}} onChange={() => {}} />);
    expect(screen.getByText('Required.')).toBeInTheDocument();
  });
});
