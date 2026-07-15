import { useState } from 'react';
import { TemplateGallery } from './TemplateGallery';
import { TemplateImporter } from './TemplateImporter';
import { TemplateExporter } from './TemplateExporter';
import { UserTemplateLibraryView } from './UserTemplateLibraryView';
import { TI } from './templateIcons';
import './templates.css';

type TemplateTab = 'browse' | 'library' | 'import' | 'export';

interface TemplatesViewProps {
  /** When set, opens straight to Export with this workflow preselected (deep-link from the editor). */
  initialExportWorkflowId?: string;
  /** Open a workflow (by id) in the editor — used to jump straight into a freshly created template. */
  onOpenWorkflow?: (workflowId: string) => void;
}

const TABS: Array<[TemplateTab, keyof typeof TI, string]> = [
  ['browse', 'grid', 'Browse'],
  ['library', 'sliders', 'Library'],
  ['import', 'download', 'Import'],
  ['export', 'upload', 'Export'],
];

export function TemplatesView({ initialExportWorkflowId, onOpenWorkflow }: TemplatesViewProps) {
  const [tab, setTab] = useState<TemplateTab>(initialExportWorkflowId ? 'export' : 'browse');

  return (
    <div className="tpl-screen">
      <div className="tpl-wrap">
        <div className="tabs">
          <div className="tabset" role="tablist">
            {TABS.map(([id, icon, label]) => (
              <button
                key={id}
                className={`tab${tab === id ? ' on' : ''}`}
                onClick={() => setTab(id)}
                role="tab"
                aria-selected={tab === id}
                aria-label={`${label} tab`}
              >
                {TI[icon]()} {label}
              </button>
            ))}
          </div>
        </div>

        {tab === 'browse' && <TemplateGallery onOpenWorkflow={onOpenWorkflow} />}
        {tab === 'library' && <UserTemplateLibraryView />}
        {tab === 'import' && <TemplateImporter />}
        {tab === 'export' && <TemplateExporter initialWorkflowId={initialExportWorkflowId} />}
      </div>
    </div>
  );
}
