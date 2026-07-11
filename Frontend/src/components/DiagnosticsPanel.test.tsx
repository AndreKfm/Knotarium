import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { DiagnosticsPanel } from './DiagnosticsPanel';
import type { CompilationDiagnostic } from '../types';

const diag = (over: Partial<Record<keyof CompilationDiagnostic, unknown>> = {}): CompilationDiagnostic => ({
  severity: 'Warning',
  code: 'WARN_X',
  message: 'something looks off',
  ...over,
}) as CompilationDiagnostic;

function setup(props: Partial<React.ComponentProps<typeof DiagnosticsPanel>> = {}) {
  const onToggleCollapse = vi.fn();
  const onFocus = vi.fn();
  render(
    <DiagnosticsPanel
      diagnostics={props.diagnostics ?? [diag()]}
      collapsed={props.collapsed ?? false}
      onToggleCollapse={onToggleCollapse}
      onFocus={onFocus}
    />,
  );
  return { onToggleCollapse, onFocus };
}

describe('DiagnosticsPanel', () => {
  it('renders nothing when there are no diagnostics', () => {
    const { container } = render(
      <DiagnosticsPanel diagnostics={[]} collapsed={false} onToggleCollapse={() => {}} onFocus={() => {}} />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('shows severity counts in the header', () => {
    setup({
      diagnostics: [
        diag({ severity: 'Error', code: 'E1' }),
        diag({ severity: 'Error', code: 'E2' }),
        diag({ severity: 'Warning', code: 'W1' }),
      ],
    });
    expect(screen.getByText('2 errors')).toBeInTheDocument();
    expect(screen.getByText('1 warnings')).toBeInTheDocument();
  });

  it('lists rows most-severe first when expanded', () => {
    setup({
      diagnostics: [
        diag({ severity: 'Info', code: 'I1', message: 'info row' }),
        diag({ severity: 'Error', code: 'E1', message: 'error row' }),
      ],
    });
    const rows = screen.getAllByTitle('Click to locate on the canvas');
    expect(rows[0]).toHaveTextContent('[E1] error row');
    expect(rows[1]).toHaveTextContent('[I1] info row');
  });

  it('hides rows when collapsed', () => {
    setup({ collapsed: true, diagnostics: [diag({ message: 'hidden row' })] });
    expect(screen.queryByTitle('Click to locate on the canvas')).toBeNull();
    // Header still present.
    expect(screen.getByRole('region', { name: 'Diagnostics' })).toBeInTheDocument();
  });

  it('calls onToggleCollapse when the header is clicked', () => {
    const { onToggleCollapse } = setup();
    fireEvent.click(screen.getByRole('button', { name: 'Collapse diagnostics' }));
    expect(onToggleCollapse).toHaveBeenCalledTimes(1);
  });

  it('calls onFocus with the clicked diagnostic', () => {
    const target = diag({ severity: 'Error', code: 'E1', message: 'click me', edgeId: 'e1' });
    const { onFocus } = setup({ diagnostics: [diag({ code: 'W1' }), target] });
    fireEvent.click(screen.getByText(/click me/));
    expect(onFocus).toHaveBeenCalledWith(target);
  });

  it('annotates a row with its edge / node location', () => {
    setup({ diagnostics: [diag({ nodeId: 'node-7', message: 'node-scoped' })] });
    expect(screen.getByText(/node node-7/)).toBeInTheDocument();
  });
});
