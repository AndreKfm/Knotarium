import { useState } from 'react';
import { BundleInstaller } from './BundleInstaller';
import { BundleExporter } from './BundleExporter';

type BundleTab = 'install' | 'export';

export function BundlesView() {
  const [tab, setTab] = useState<BundleTab>('install');

  return (
    <div className="kgb" style={{ height: '100%', overflowY: 'auto' }}>
      <div className="tabs" style={{ paddingTop: 30 }}>
        <div className="seg" role="tablist">
          <button className={tab === 'install' ? 'on' : ''} onClick={() => setTab('install')} aria-label="Install tab">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 3v12" /><path d="m7 10 5 5 5-5" /><path d="M5 21h14" /></svg>
            Install
          </button>
          <button className={tab === 'export' ? 'on' : ''} onClick={() => setTab('export')} aria-label="Export tab">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 21V9" /><path d="m7 14 5-5 5 5" /><path d="M5 3h14" /></svg>
            Export
          </button>
        </div>
      </div>
      {tab === 'install' ? <BundleInstaller /> : <BundleExporter />}
    </div>
  );
}
