// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useState } from 'react';
import { Bell, Box, Database, FolderLock, HardDrive, Plug, ShieldAlert, Sparkles } from 'lucide-react';
import { ErrorWorkflowSetting } from './ErrorWorkflowSetting';
import { NotificationChannelManager } from './NotificationChannelManager';
import { BackupRestorePanel } from './BackupRestorePanel';
import { ExternalSystemsManager } from './ExternalSystemsManager';
import { AiProviderSetting } from './AiProviderSetting';
import { FileAccessSetting } from './FileAccessSetting';
import { usePendingFileAccessGrantStore } from '../stores/usePendingFileAccessGrantStore';
import { CapabilitiesSetting } from './CapabilitiesSetting';
import { SandboxSetting } from './SandboxSetting';
import { RetentionSetting } from './RetentionSetting';

type SettingsSection = 'notifications' | 'ai' | 'fileAccess' | 'capabilities' | 'sandbox' | 'retention' | 'systems' | 'backup';

const SECTIONS: { key: SettingsSection; label: string; icon: typeof Bell }[] = [
  { key: 'notifications', label: 'Notifications', icon: Bell },
  { key: 'ai', label: 'AI Provider', icon: Sparkles },
  { key: 'fileAccess', label: 'File Access', icon: FolderLock },
  { key: 'capabilities', label: 'Capabilities', icon: ShieldAlert },
  { key: 'sandbox', label: 'Sandbox', icon: Box },
  { key: 'retention', label: 'Retention', icon: HardDrive },
  { key: 'systems', label: 'External Systems', icon: Plug },
  { key: 'backup', label: 'Backup & Restore', icon: Database },
];

interface SettingsViewProps {
  /** Shared runtime-arming state (also drives the top-bar pill). `null` while unknown. */
  armed: boolean | null;
  /** Disarm the runtime — flips the same shared state the top bar shows. */
  onDisarm: () => void;
}

/**
 * Instance Settings shell: a left sub-nav rail separating distinct admin concerns onto their own
 * pages. "Notifications" holds the failure-alerting config (error workflow + channels); "Backup &
 * Restore" is instance-level data management. Keeping them apart is the point — a destructive,
 * secret-bearing restore shouldn't share a screen with a webhook test button.
 */
export function SettingsView({ armed, onDisarm }: SettingsViewProps) {
  // Land on File Access when arriving via a run's "Grant this path" CTA (a path is queued in the store).
  const [section, setSection] = useState<SettingsSection>(
    () => (usePendingFileAccessGrantStore.getState().pendingPath ? 'fileAccess' : 'notifications'),
  );
  const active = SECTIONS.find((s) => s.key === section) ?? SECTIONS[0];

  return (
    <div className="iset">
      <aside className="iset-rail">
        <div className="iset-kick">Instance Settings</div>
        {SECTIONS.map(({ key, label, icon: Icon }) => (
          <button
            key={key}
            type="button"
            className={'iset-item' + (key === section ? ' on' : '')}
            aria-current={key === section ? 'page' : undefined}
            onClick={() => setSection(key)}
          >
            <Icon size={16} />
            {label}
          </button>
        ))}
      </aside>

      <main className="iset-main">
        <div className="iset-inner">
          <div className="iset-crumb"><b>Instance Settings</b> / {active.label}</div>

          {section === 'notifications' && (
            <>
              <div className="iset-head">
                <h1><span className="ih-ic"><Bell size={20} /></span> Notifications</h1>
                <p>
                  Configure how this instance reacts when a workflow fails — the global error workflow and the
                  channels that receive failure alerts.
                </p>
              </div>
              <ErrorWorkflowSetting />
              <NotificationChannelManager />
            </>
          )}

          {section === 'ai' && (
            <>
              <div className="iset-head">
                <h1><span className="ih-ic"><Sparkles size={20} /></span> AI Provider</h1>
                <p>
                  Choose the LLM vendor and model used by “Generate with AI”, and the encrypted credential
                  holding its API key.
                </p>
              </div>
              <AiProviderSetting />
            </>
          )}

          {section === 'fileAccess' && (
            <>
              <div className="iset-head">
                <h1><span className="ih-ic"><FolderLock size={20} /></span> File Access</h1>
                <p>
                  Control which directories the File Read / File Write nodes may touch, reserve free disk
                  space for writes, and — if you must — grant unrestricted access. Denied by default.
                </p>
              </div>
              <FileAccessSetting />
            </>
          )}

          {section === 'capabilities' && (
            <>
              <div className="iset-head">
                <h1><span className="ih-ic"><ShieldAlert size={20} /></span> Capabilities</h1>
                <p>
                  Master on/off switches for privileged node capabilities — inline code execution and
                  database access. Off by default; enable only what you trust every workflow to use.
                </p>
              </div>
              <CapabilitiesSetting />
            </>
          )}

          {section === 'sandbox' && (
            <>
              <div className="iset-head">
                <h1><span className="ih-ic"><Box size={20} /></span> Sandbox</h1>
                <p>
                  Decide where user-authored C# — the Inline Code node and custom node packages — runs,
                  and how hard the operating system confines it. Complements the Capabilities switch:
                  that decides <i>whether</i> code may run, this decides <i>how safely</i>.
                </p>
              </div>
              <SandboxSetting />
            </>
          )}

          {section === 'retention' && (
            <>
              <div className="iset-head">
                <h1><span className="ih-ic"><HardDrive size={20} /></span> Retention</h1>
                <p>
                  Bound how large the database can grow so it can't fill the disk — prune old finished runs
                  and their logs, and cap version histories and the audit log. Applies on the next sweep.
                </p>
              </div>
              <RetentionSetting />
            </>
          )}

          {section === 'systems' && <ExternalSystemsManager />}

          {section === 'backup' && (
            <BackupRestorePanel armed={armed} onDisarm={onDisarm} />
          )}
        </div>
      </main>
    </div>
  );
}
