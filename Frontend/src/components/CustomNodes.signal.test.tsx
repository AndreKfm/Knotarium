import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { renderPropertiesSummary } from './CustomNodes.helpers';

describe('External device node signal summary', () => {
  it('shows the event name for an eventTrigger', () => {
    render(<>{renderPropertiesSummary('eventTrigger', { event: 'Bildquelle_Infosystem:Umschaltung', instance: 'cam-server' })}</>);
    expect(screen.getByText('Bildquelle_Infosystem:Umschaltung')).toBeInTheDocument();
    expect(screen.getByText('cam-server')).toBeInTheDocument(); // non-default target shown
  });

  it('shows the action name for a fireAction', () => {
    render(<>{renderPropertiesSummary('fireAction', { action: 'CrossSwitch 951->875', instance: 'default' })}</>);
    expect(screen.getByText('CrossSwitch 951->875')).toBeInTheDocument();
    expect(screen.queryByText('default')).not.toBeInTheDocument(); // the default target is not labeled
  });

  it('an eventTrigger with no event listens to "any event"', () => {
    render(<>{renderPropertiesSummary('eventTrigger', {})}</>);
    expect(screen.getByText(/any event/i)).toBeInTheDocument();
  });

  it('a fireAction with no action is flagged as unconfigured', () => {
    render(<>{renderPropertiesSummary('fireAction', {})}</>);
    expect(screen.getByText(/no action/i)).toBeInTheDocument();
  });

  it('reads the label from an editor-picked resourceLocator (object) action + instance', () => {
    render(<>{renderPropertiesSummary('fireAction', {
      action: { value: 'CustomAction', label: 'Custom Action', mode: 'list' },
      instance: { value: 'siteA', label: 'Site A (main site)', mode: 'list' },
    })}</>);
    expect(screen.getByText('Custom Action')).toBeInTheDocument();
    expect(screen.getByText('Site A (main site)')).toBeInTheDocument();
    expect(screen.queryByText(/no action/i)).not.toBeInTheDocument();
  });

  it('falls back to the value when a locator has no label', () => {
    render(<>{renderPropertiesSummary('fireAction', { action: { value: 'CustomAction', mode: 'manual' } })}</>);
    expect(screen.getByText('CustomAction')).toBeInTheDocument();
  });
});
