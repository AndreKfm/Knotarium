// Copyright 2026 Andre Kaufmann
// SPDX-License-Identifier: Apache-2.0

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import Editor from '@monaco-editor/react';
import * as YAML from 'yaml';
import { 
  Play, 
  Upload, 
  Plus, 
  Terminal, 
  AlertCircle, 
  ArrowLeft, 
  CheckCircle2, 
  XCircle, 
  Sparkles, 
  Code2, 
  FileCode, 
  ChevronRight, 
  Info,
  FolderOpen
} from 'lucide-react';
import { api } from '../utils/api';
import { ManifestForm } from '../components/shared/ManifestForm';
import { 
  DEFAULT_DECLARATIVE_MANIFEST, 
  DEFAULT_DECLARATIVE_TESTS, 
  DEFAULT_COMPILED_MANIFEST, 
  DEFAULT_COMPILED_EXECUTOR, 
  DEFAULT_COMPILED_TESTS 
} from './Templates';

interface NodePackage {
  id: string;
  displayName: string;
  category: string;
  versions: Array<{
    id: string;
    nodePackageId: string;
    version: string;
    manifestJson: string;
    source: string;
    capabilities: string[];
    createdAt: string;
  }>;
}

interface EditorState {
  manifestYaml: string;
  executorCode: string;
  testsYaml: string;
  activeTab: 'manifest' | 'executor' | 'tests';
}

type ManifestParameter = {
  name: string;
  type: 'string' | 'number' | 'boolean' | 'enum' | 'credentialRef';
  default?: string | number | boolean | null;
};

type ParsedManifest = {
  id?: string;
  displayName?: string;
  category?: string;
  tier?: 'declarative' | 'compiled';
  version?: string;
  parameters?: ManifestParameter[];
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function toManifestParameter(value: unknown): ManifestParameter | null {
  if (
    !isRecord(value)
    || typeof value.name !== 'string'
    || (value.type !== 'string' && value.type !== 'number' && value.type !== 'boolean' && value.type !== 'enum' && value.type !== 'credentialRef')
  ) {
    return null;
  }

  return {
    name: value.name,
    type: value.type,
    default: typeof value.default === 'string' || typeof value.default === 'number' || typeof value.default === 'boolean' || value.default === null
      ? value.default
      : undefined,
  };
}

function toParsedManifest(value: unknown): ParsedManifest | null {
  if (!isRecord(value)) {
    return null;
  }

  return {
    id: typeof value.id === 'string' ? value.id : undefined,
    displayName: typeof value.displayName === 'string' ? value.displayName : undefined,
    category: typeof value.category === 'string' ? value.category : undefined,
    tier: value.tier === 'compiled' ? 'compiled' : 'declarative',
    version: typeof value.version === 'string' ? value.version : undefined,
    parameters: Array.isArray(value.parameters)
      ? value.parameters.map(toManifestParameter).filter((parameter): parameter is ManifestParameter => parameter !== null)
      : undefined,
  };
}

function parseManifestJson(manifestJson: string): ParsedManifest | null {
  try {
    return toParsedManifest(JSON.parse(manifestJson || '{}'));
  } catch {
    return null;
  }
}

function getTextInputValue(value: unknown, fallback: unknown): string | number {
  if (typeof value === 'string' || typeof value === 'number') {
    return value;
  }

  if (typeof fallback === 'string' || typeof fallback === 'number') {
    return fallback;
  }

  if (typeof value === 'boolean') {
    return value ? 'true' : 'false';
  }

  if (typeof fallback === 'boolean') {
    return fallback ? 'true' : 'false';
  }

  return '';
}

export function NodeEditorShell({ onBack }: { onBack: () => void }) {
  const [packages, setPackages] = useState<NodePackage[]>([]);
  const [selectedPackage, setSelectedPackage] = useState<NodePackage | null>(null);
  
  // Wizard Modal state
  const [showWizard, setShowWizard] = useState(false);
  const [newId, setNewId] = useState('');
  const [newDisplayName, setNewDisplayName] = useState('');
  const [newCategory, setNewCategory] = useState('Utility');
  const [newTier, setNewTier] = useState<'declarative' | 'compiled'>('declarative');

  // Editor states
  const [editorState, setEditorState] = useState<EditorState>({
    manifestYaml: '',
    executorCode: '',
    testsYaml: '',
    activeTab: 'manifest'
  });

  // YAML Parsing & Form preview state
  const [previewProperties, setPreviewProperties] = useState<Record<string, unknown>>({});

  // Bottom panel terminal test runner states
  const [terminalTab, setTerminalTab] = useState<'inputs' | 'console'>('inputs');
  const [isRunningTests, setIsRunningTests] = useState(false);
  const [testResults, setTestResults] = useState<{
    success: boolean;
    logs: string[];
    cases: Array<{ name: string; status: 'pass' | 'fail'; message: string }>;
  } | null>(null);

  // Status message
  const [publishStatus, setPublishStatus] = useState<{ type: 'success' | 'error'; message: string } | null>(null);

  const selectedPackageRef = useRef<NodePackage | null>(null);

  useEffect(() => {
    selectedPackageRef.current = selectedPackage;
  }, [selectedPackage]);

  const parsedManifestState = useMemo(() => {
    if (!editorState.manifestYaml) {
      return { parsedManifest: null as ParsedManifest | null, yamlError: null as string | null };
    }

    try {
      const parsed = toParsedManifest(YAML.parse(editorState.manifestYaml));
      if (parsed) {
        return { parsedManifest: parsed, yamlError: null };
      }

      return { parsedManifest: null, yamlError: 'Parsed content is not a valid YAML object' };
    } catch (error) {
      return {
        parsedManifest: null,
        yamlError: error instanceof Error ? error.message : 'YAML parsing syntax error',
      };
    }
  }, [editorState.manifestYaml]);

  const { parsedManifest, yamlError } = parsedManifestState;

  const applySelectedPackage = useCallback((pkg: NodePackage) => {
    setSelectedPackage(pkg);
    const latestVersion = pkg.versions[pkg.versions.length - 1];
    const parsedLatestManifest = parseManifestJson(latestVersion.manifestJson);
    const tier = parsedLatestManifest?.tier === 'compiled' ? 'compiled' : 'declarative';
    const manifestYaml = parsedLatestManifest ? YAML.stringify(parsedLatestManifest) : DEFAULT_DECLARATIVE_MANIFEST;
    const executorCode = latestVersion.source || (tier === 'compiled' ? DEFAULT_COMPILED_EXECUTOR : '');
    const testsYaml = tier === 'compiled' ? DEFAULT_COMPILED_TESTS : DEFAULT_DECLARATIVE_TESTS;

    setEditorState({
      manifestYaml,
      executorCode,
      testsYaml,
      activeTab: 'manifest'
    });
    setPublishStatus(null);
    setTestResults(null);
    setTerminalTab('inputs');
  }, []);

  const loadPackages = useCallback(async () => {
    try {
      const data = await api.getNodePackages();
      setPackages(data);
      if (data.length > 0 && !selectedPackageRef.current) {
        applySelectedPackage(data[0]);
      }
    } catch (err) {
      console.error('Failed to load node packages:', err);
    }
  }, [applySelectedPackage]);

  // Load packages on mount
  useEffect(() => {
    let isCancelled = false;

    async function loadInitialPackages() {
      try {
        const data = await api.getNodePackages();
        if (isCancelled) {
          return;
        }

        setPackages(data);
        if (data.length > 0 && !selectedPackageRef.current) {
          applySelectedPackage(data[0]);
        }
      } catch (err) {
        if (!isCancelled) {
          console.error('Failed to load node packages:', err);
        }
      }
    }

    void loadInitialPackages();

    return () => {
      isCancelled = true;
    };
  }, [applySelectedPackage]);

  const handleSelectPackage = (pkg: NodePackage) => {
    applySelectedPackage(pkg);
  };

  // Handle Wizard creation of new Node package
  const handleCreateNode = () => {
    if (!newId || !newDisplayName) {
      alert('Please fill out all required fields');
      return;
    }

    const packageId = newId.trim().toLowerCase().replace(/\s+/g, '.');
    
    // Check if duplicate
    if (packages.some(p => p.id === packageId)) {
      alert(`A package with ID '${packageId}' already exists.`);
      return;
    }

    const manifestYaml = (newTier === 'declarative' ? DEFAULT_DECLARATIVE_MANIFEST : DEFAULT_COMPILED_MANIFEST)
      .replace(newTier === 'declarative' ? 'id: custom.declarative.node' : 'id: custom.compiled.node', `id: ${packageId}`)
      .replace(newTier === 'declarative' ? 'displayName: Custom Declarative Node' : 'displayName: Custom Compiled Node', `displayName: ${newDisplayName}`)
      .replace('category: Utility', `category: ${newCategory}`);
    const executorCode = newTier === 'compiled' ? DEFAULT_COMPILED_EXECUTOR : '';
    const testsYaml = newTier === 'compiled' ? DEFAULT_COMPILED_TESTS : DEFAULT_DECLARATIVE_TESTS;

    const newPkg: NodePackage = {
      id: packageId,
      displayName: newDisplayName,
      category: newCategory,
      versions: [
        {
          id: 'draft',
          nodePackageId: packageId,
          version: '1.0.0-draft',
          manifestJson: JSON.stringify({
            id: packageId,
            version: '1.0.0',
            displayName: newDisplayName,
            category: newCategory,
            tier: newTier,
            capabilities: ['logging']
          }),
          source: executorCode,
          capabilities: ['logging'],
          createdAt: new Date().toISOString()
        }
      ]
    };

    setPackages(prev => [newPkg, ...prev]);
    setSelectedPackage(newPkg);
    setEditorState({
      manifestYaml,
      executorCode,
      testsYaml,
      activeTab: 'manifest'
    });
    
    // Close modal & reset wizard state
    setShowWizard(false);
    setNewId('');
    setNewDisplayName('');
    setNewCategory('Utility');
    setNewTier('declarative');
    setPublishStatus(null);
    setTestResults(null);
    setTerminalTab('inputs');
  };

  // Run tests in sandbox via backend Roslyn runner
  const handleRunTests = async () => {
    setIsRunningTests(true);
    setTerminalTab('console');
    setTestResults(null);

    // Dynamic timeout simulations for extreme visual UI premium feeling
    await new Promise(r => setTimeout(r, 1200));

    try {
      const packageId = selectedPackage?.id || parsedManifest?.id || 'draft-package';
      const result = await api.testNodePackage(
        packageId,
        editorState.manifestYaml,
        editorState.executorCode,
        editorState.testsYaml
      );
      const resultRecord = typeof result === 'object' && result !== null
        ? (result as { success?: unknown; logs?: unknown; cases?: unknown })
        : {};

      setTestResults({
        success: Boolean(resultRecord.success),
        logs: Array.isArray(resultRecord.logs) ? resultRecord.logs : [],
        cases: Array.isArray(resultRecord.cases) ? resultRecord.cases : []
      });
    } catch (error: unknown) {
      console.error(error);
      setTestResults({
        success: false,
        logs: [`[FATAL] Sandbox runner exception occurred: ${error instanceof Error ? error.message : String(error)}`],
        cases: [{ name: 'Sandbox Exception', status: 'fail', message: 'Fatal crash inside executor sandbox run.' }]
      });
    } finally {
      setIsRunningTests(false);
    }
  };

  // Publish node package draft to backend API
  const handlePublishPackage = async () => {
    if (!selectedPackage || !parsedManifest) return;

    if (!testResults || !testResults.success) {
      setPublishStatus({
        type: 'error',
        message: 'Mandatory Gate Violated: You must successfully run and pass all sandbox tests before publishing.'
      });
      return;
    }

    try {
      // Create FormData to mirror multipart post spec from B4/D2
      const formData = new FormData();
      const manifestJson = JSON.stringify(parsedManifest);
      
      const pkgFile = new Blob([editorState.manifestYaml], { type: 'text/yaml' });
      formData.append('package', pkgFile, `${selectedPackage.id}.zip`); // zip binary wrapper stub
      formData.append('displayName', parsedManifest.displayName || selectedPackage.displayName);
      formData.append('category', parsedManifest.category || selectedPackage.category);
      formData.append('packageId', selectedPackage.id);
      formData.append('version', parsedManifest.version || '1.0.0');
      formData.append('manifestJson', manifestJson);
      formData.append('sourceCode', editorState.executorCode);

      await api.publishNodePackage(formData);

      setPublishStatus({
        type: 'success',
        message: `Package ${selectedPackage.id} published successfully! Version ${parsedManifest.version || '1.0.0'} hot-loaded into editor registry.`
      });
      loadPackages();
    } catch (err) {
      console.error(err);
      setPublishStatus({
        type: 'error',
        message: `Backend Publication Failure: ${err instanceof Error ? err.message : 'Verification rejected draft packaging.'}`
      });
    }
  };

  return (
    <div style={{ display: 'flex', height: '100%', width: '100%', background: 'var(--bg-main)', overflow: 'hidden' }}>
      
      {/* 1. Left Sidebar: Packages List & Creation wizard */}
      <div 
        style={{
          width: '280px',
          borderRight: '1px solid var(--border-color)',
          background: 'rgba(10, 13, 22, 0.4)',
          display: 'flex',
          flexDirection: 'column',
          height: '100%'
        }}
      >
        <div style={{ padding: '20px', borderBottom: '1px solid var(--border-color)', display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
            <FolderOpen size={16} className="text-secondary" style={{ color: 'var(--color-accent)' }} />
            <span style={{ fontSize: '0.85rem', fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.05em', color: 'var(--text-primary)' }}>Node Packages</span>
          </div>
          <button 
            onClick={() => setShowWizard(true)}
            style={{
              background: 'rgba(99, 102, 241, 0.1)',
              border: '1px solid rgba(99, 102, 241, 0.3)',
              color: '#fff',
              padding: '6px',
              borderRadius: '6px',
              cursor: 'pointer',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              transition: 'all 0.2s',
            }}
            title="Create New Node Package"
            className="hover-glow"
          >
            <Plus size={14} />
          </button>
        </div>

        <div style={{ flex: 1, overflowY: 'auto', padding: '10px', display: 'flex', flexDirection: 'column', gap: '6px' }}>
          {packages.map(pkg => {
            const isSelected = selectedPackage?.id === pkg.id;
            const latestVer = pkg.versions[pkg.versions.length - 1];
            const manifestObj = parseManifestJson(latestVer.manifestJson);
            const isCompiled = manifestObj?.tier === 'compiled';

            return (
              <div
                key={pkg.id}
                onClick={() => handleSelectPackage(pkg)}
                style={{
                  padding: '12px',
                  borderRadius: '8px',
                  cursor: 'pointer',
                  background: isSelected ? 'rgba(99, 102, 241, 0.08)' : 'transparent',
                  border: isSelected ? '1px solid var(--color-accent)' : '1px solid transparent',
                  transition: 'all 0.2s ease',
                  display: 'flex',
                  flexDirection: 'column',
                  gap: '4px'
                }}
                className="package-item-hover"
              >
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <span style={{ fontWeight: 600, fontSize: '0.85rem', color: isSelected ? '#fff' : 'var(--text-primary)' }}>
                    {pkg.displayName}
                  </span>
                  <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)' }}>
                    v{latestVer.version}
                  </span>
                </div>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', fontSize: '0.7rem' }}>
                  <span style={{ color: 'var(--text-secondary)' }}>{pkg.id}</span>
                  <span style={{ 
                    padding: '2px 6px', 
                    borderRadius: '4px', 
                    background: isCompiled ? 'rgba(6, 182, 212, 0.15)' : 'rgba(16, 185, 129, 0.15)', 
                    color: isCompiled ? 'var(--color-info)' : 'var(--color-success)',
                    fontSize: '0.65rem',
                    fontWeight: 600
                  }}>
                    {isCompiled ? 'compiled' : 'declarative'}
                  </span>
                </div>
              </div>
            );
          })}
        </div>
      </div>

      {/* 2. Main Authoring Workspace Container */}
      <div style={{ flex: 1, display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
        
        {/* Workspace Top Header Bar */}
        <div 
          style={{
            height: '60px',
            borderBottom: '1px solid var(--border-color)',
            background: 'rgba(16, 22, 37, 0.4)',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            padding: '0 20px'
          }}
        >
          <div style={{ display: 'flex', alignItems: 'center', gap: '16px' }}>
            <button 
              onClick={onBack}
              style={{
                background: 'transparent',
                border: 'none',
                color: 'var(--text-secondary)',
                cursor: 'pointer',
                display: 'flex',
                alignItems: 'center',
                gap: '6px',
                fontSize: '0.85rem'
              }}
            >
              <ArrowLeft size={16} />
              Back
            </button>
            <div style={{ height: '20px', width: '1px', background: 'var(--border-color)' }}></div>
            {selectedPackage ? (
              <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
                <span style={{ fontWeight: 700, fontSize: '0.95rem', color: '#fff' }}>{selectedPackage.displayName}</span>
                <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)', fontFamily: 'monospace' }}>({selectedPackage.id})</span>
              </div>
            ) : (
              <span style={{ color: 'var(--text-muted)' }}>No package selected</span>
            )}
          </div>

          <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
            <button
              onClick={handleRunTests}
              disabled={isRunningTests || !selectedPackage}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: '8px',
                padding: '8px 14px',
                borderRadius: '8px',
                background: 'rgba(16, 185, 129, 0.1)',
                border: '1px solid rgba(16, 185, 129, 0.3)',
                color: 'var(--color-success)',
                cursor: isRunningTests ? 'not-allowed' : 'pointer',
                fontSize: '0.8rem',
                fontWeight: 600,
                opacity: isRunningTests ? 0.6 : 1,
                transition: 'all 0.2s'
              }}
            >
              <Play size={14} fill="var(--color-success)" />
              {isRunningTests ? 'Running Sandbox...' : 'Run Tests'}
            </button>

            <button
              onClick={handlePublishPackage}
              disabled={!selectedPackage}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: '8px',
                padding: '8px 14px',
                borderRadius: '8px',
                background: 'linear-gradient(135deg, var(--color-accent), #4f46e5)',
                border: 'none',
                color: '#fff',
                cursor: 'pointer',
                fontSize: '0.8rem',
                fontWeight: 600,
                transition: 'all 0.2s',
                boxShadow: '0 0 10px rgba(99, 102, 241, 0.2)'
              }}
            >
              <Upload size={14} />
              Publish Node
            </button>
          </div>
        </div>

        {/* Workspace Panels Split Layout */}
        <div style={{ flex: 1, display: 'flex', overflow: 'hidden' }}>
          
          {/* Main workspace (Monaco Editor & Bottom Terminal) */}
          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', height: '100%', borderRight: '1px solid var(--border-color)' }}>
            
            {/* Monaco Tab Bar */}
            <div style={{ display: 'flex', background: 'rgba(0, 0, 0, 0.2)', borderBottom: '1px solid var(--border-color)', height: '40px' }}>
              <button
                onClick={() => setEditorState(p => ({ ...p, activeTab: 'manifest' }))}
                style={{
                  padding: '0 20px',
                  background: editorState.activeTab === 'manifest' ? 'rgba(255, 255, 255, 0.05)' : 'transparent',
                  border: 'none',
                  borderBottom: editorState.activeTab === 'manifest' ? '2px solid var(--color-accent)' : '2px solid transparent',
                  color: editorState.activeTab === 'manifest' ? '#fff' : 'var(--text-secondary)',
                  cursor: 'pointer',
                  fontSize: '0.8rem',
                  fontWeight: 600,
                  display: 'flex',
                  alignItems: 'center',
                  gap: '6px'
                }}
              >
                <Code2 size={14} />
                manifest.yaml
              </button>

              <button
                onClick={() => setEditorState(p => ({ ...p, activeTab: 'executor' }))}
                disabled={parsedManifest?.tier !== 'compiled'}
                style={{
                  padding: '0 20px',
                  background: editorState.activeTab === 'executor' ? 'rgba(255, 255, 255, 0.05)' : 'transparent',
                  border: 'none',
                  borderBottom: editorState.activeTab === 'executor' ? '2px solid var(--color-accent)' : '2px solid transparent',
                  color: parsedManifest?.tier !== 'compiled' ? 'var(--text-muted)' : (editorState.activeTab === 'executor' ? '#fff' : 'var(--text-secondary)'),
                  cursor: parsedManifest?.tier !== 'compiled' ? 'not-allowed' : 'pointer',
                  fontSize: '0.8rem',
                  fontWeight: 600,
                  display: 'flex',
                  alignItems: 'center',
                  gap: '6px'
                }}
                title={parsedManifest?.tier !== 'compiled' ? 'Only enabled for Compiled (Tier 2) packages' : ''}
              >
                <FileCode size={14} />
                Executor.cs
                {parsedManifest?.tier !== 'compiled' && (
                  <span style={{ fontSize: '0.65rem', padding: '1px 4px', borderRadius: '4px', background: 'rgba(255,255,255,0.05)', color: 'var(--text-muted)' }}>Locked</span>
                )}
              </button>

              <button
                onClick={() => setEditorState(p => ({ ...p, activeTab: 'tests' }))}
                style={{
                  padding: '0 20px',
                  background: editorState.activeTab === 'tests' ? 'rgba(255, 255, 255, 0.05)' : 'transparent',
                  border: 'none',
                  borderBottom: editorState.activeTab === 'tests' ? '2px solid var(--color-accent)' : '2px solid transparent',
                  color: editorState.activeTab === 'tests' ? '#fff' : 'var(--text-secondary)',
                  cursor: 'pointer',
                  fontSize: '0.8rem',
                  fontWeight: 600,
                  display: 'flex',
                  alignItems: 'center',
                  gap: '6px'
                }}
              >
                <Terminal size={14} />
                tests/cases.yaml
              </button>
            </div>

            {/* Monaco Editor Component */}
            <div style={{ flex: 1, minHeight: 0, position: 'relative' }}>
              {selectedPackage ? (
                <>
                  {editorState.activeTab === 'manifest' && (
                    <Editor
                      height="100%"
                      defaultLanguage="yaml"
                      theme="vs-dark"
                      value={editorState.manifestYaml}
                      onChange={(val) => setEditorState(p => ({ ...p, manifestYaml: val || '' }))}
                      options={{
                        minimap: { enabled: false },
                        fontSize: 13,
                        lineHeight: 20,
                        fontFamily: "'JetBrains Mono', monospace",
                        scrollBeyondLastLine: false,
                        automaticLayout: true
                      }}
                    />
                  )}
                  {editorState.activeTab === 'executor' && parsedManifest?.tier === 'compiled' && (
                    <Editor
                      height="100%"
                      defaultLanguage="csharp"
                      theme="vs-dark"
                      value={editorState.executorCode}
                      onChange={(val) => setEditorState(p => ({ ...p, executorCode: val || '' }))}
                      options={{
                        minimap: { enabled: false },
                        fontSize: 13,
                        lineHeight: 20,
                        fontFamily: "'JetBrains Mono', monospace",
                        scrollBeyondLastLine: false,
                        automaticLayout: true
                      }}
                    />
                  )}
                  {editorState.activeTab === 'tests' && (
                    <Editor
                      height="100%"
                      defaultLanguage="yaml"
                      theme="vs-dark"
                      value={editorState.testsYaml}
                      onChange={(val) => setEditorState(p => ({ ...p, testsYaml: val || '' }))}
                      options={{
                        minimap: { enabled: false },
                        fontSize: 13,
                        lineHeight: 20,
                        fontFamily: "'JetBrains Mono', monospace",
                        scrollBeyondLastLine: false,
                        automaticLayout: true
                      }}
                    />
                  )}
                </>
              ) : (
                <div style={{ height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', color: 'var(--text-muted)', gap: '10px' }}>
                  <Info size={32} />
                  <span>Select a package or create a new one to begin authoring.</span>
                </div>
              )}
            </div>

            {/* Bottom Panel: Sandbox Test Runner Terminal */}
            <div 
              style={{
                height: '240px',
                borderTop: '1px solid var(--border-color)',
                background: 'rgba(10, 13, 22, 0.95)',
                display: 'flex',
                flexDirection: 'column'
              }}
            >
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', height: '36px', background: 'rgba(0,0,0,0.3)', borderBottom: '1px solid var(--border-color)', padding: '0 16px' }}>
                <div style={{ display: 'flex', gap: '16px' }}>
                  <button 
                    onClick={() => setTerminalTab('inputs')}
                    style={{
                      background: 'transparent',
                      border: 'none',
                      color: terminalTab === 'inputs' ? 'var(--color-accent)' : 'var(--text-secondary)',
                      fontSize: '0.75rem',
                      fontWeight: 700,
                      cursor: 'pointer',
                      display: 'flex',
                      alignItems: 'center',
                      gap: '4px'
                    }}
                  >
                    <Info size={12} />
                    MOCK INPUTS
                  </button>
                  <button 
                    onClick={() => setTerminalTab('console')}
                    style={{
                      background: 'transparent',
                      border: 'none',
                      color: terminalTab === 'console' ? 'var(--color-accent)' : 'var(--text-secondary)',
                      fontSize: '0.75rem',
                      fontWeight: 700,
                      cursor: 'pointer',
                      display: 'flex',
                      alignItems: 'center',
                      gap: '4px'
                    }}
                  >
                    <Terminal size={12} />
                    RUN CONSOLE
                  </button>
                </div>
                
                {publishStatus && (
                  <div style={{ 
                    fontSize: '0.75rem', 
                    color: publishStatus.type === 'success' ? 'var(--color-success)' : 'var(--color-error)',
                    display: 'flex', 
                    alignItems: 'center', 
                    gap: '6px',
                    animation: 'fadeIn 0.3s'
                  }}>
                    {publishStatus.type === 'success' ? <CheckCircle2 size={12} /> : <AlertCircle size={12} />}
                    {publishStatus.message}
                  </div>
                )}
              </div>

              {/* Terminal View Content */}
              <div style={{ flex: 1, overflowY: 'auto', padding: '16px', fontFamily: "'JetBrains Mono', monospace", fontSize: '0.8rem' }}>
                
                {terminalTab === 'inputs' && (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
                    <p style={{ color: 'var(--text-secondary)', fontSize: '0.75rem' }}>Configure test arguments mapping dynamically to inputs declared in the manifest:</p>
                    <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '10px' }}>
                      {parsedManifest?.parameters && parsedManifest.parameters.length > 0 ? (
                        parsedManifest.parameters.map((param) => (
                          <div key={param.name} style={{ display: 'flex', flexDirection: 'column', gap: '4px' }}>
                            <span style={{ fontSize: '0.7rem', color: '#fff', fontWeight: 600 }}>{param.name} ({param.type})</span>
                            <input
                              type="text"
                              value={getTextInputValue(previewProperties[param.name], param.default)}
                              onChange={(e) => setPreviewProperties(prev => ({ ...prev, [param.name]: e.target.value }))}
                              placeholder={`Mock input value...`}
                              style={{
                                padding: '6px 10px',
                                background: 'rgba(0,0,0,0.3)',
                                border: '1px solid var(--border-color)',
                                borderRadius: '4px',
                                color: '#fff',
                                fontSize: '0.75rem',
                                outline: 'none'
                              }}
                            />
                          </div>
                        ))
                      ) : (
                        <p style={{ gridColumn: 'span 2', color: 'var(--text-muted)', fontSize: '0.75rem' }}>No parameter arguments declared to mock.</p>
                      )}
                    </div>
                  </div>
                )}

                {terminalTab === 'console' && (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
                    {isRunningTests ? (
                      <div style={{ display: 'flex', alignItems: 'center', gap: '10px', color: 'var(--color-warning)' }}>
                        <div className="spinner" style={{ width: '14px', height: '14px', border: '2px solid rgba(255,255,255,0.1)', borderTopColor: 'var(--color-warning)', borderRadius: '50%', animation: 'spin 1s infinite linear' }}></div>
                        <span>[EXECUTING] Compiling assemblies and launching dynamic test cases sandbox...</span>
                      </div>
                    ) : testResults ? (
                      <div style={{ display: 'flex', flexDirection: 'column', gap: '12px' }}>
                        
                        {/* Test Status Badge Summary */}
                        <div style={{ display: 'flex', alignItems: 'center', gap: '10px', paddingBottom: '10px', borderBottom: '1px solid rgba(255,255,255,0.05)' }}>
                          {testResults.success ? (
                            <span style={{ display: 'inline-flex', alignItems: 'center', gap: '6px', color: 'var(--color-success)', fontWeight: 700 }}>
                              <CheckCircle2 size={16} />
                              VERIFICATION PASSED (ALL GREEN)
                            </span>
                          ) : (
                            <span style={{ display: 'inline-flex', alignItems: 'center', gap: '6px', color: 'var(--color-error)', fontWeight: 700 }}>
                              <XCircle size={16} />
                              VERIFICATION FAILED (RED BLOCK)
                            </span>
                          )}
                        </div>

                        {/* Case Results List */}
                        <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                          {testResults.cases.map((c, i) => (
                            <div key={i} style={{ display: 'flex', alignItems: 'center', gap: '8px', paddingLeft: '8px' }}>
                              <ChevronRight size={12} className="text-secondary" />
                              <span style={{ color: c.status === 'pass' ? 'var(--color-success)' : 'var(--color-error)', fontWeight: 600 }}>
                                [{c.status.toUpperCase()}]
                              </span>
                              <span style={{ color: '#fff' }}>{c.name}</span>
                              <span style={{ color: 'var(--text-secondary)', fontSize: '0.75rem' }}>- {c.message}</span>
                            </div>
                          ))}
                        </div>

                        {/* Exec Journal Outputs */}
                        <div style={{ background: 'rgba(0,0,0,0.4)', padding: '10px', borderRadius: '6px', border: '1px solid rgba(255,255,255,0.03)' }}>
                          <span style={{ fontSize: '0.7rem', color: 'var(--text-muted)', display: 'block', marginBottom: '6px' }}>SANDBOX LOG JOURNAL:</span>
                          <div style={{ display: 'flex', flexDirection: 'column', gap: '4px', maxHeight: '100px', overflowY: 'auto' }}>
                            {testResults.logs.map((log, index) => {
                              let logColor = 'var(--text-secondary)';
                              if (log.includes('[ERROR]')) logColor = 'var(--color-error)';
                              if (log.includes('[RUNNER]')) logColor = 'var(--color-info)';
                              if (log.includes('[EXECUTOR]')) logColor = '#fff';
                              return (
                                <div key={index} style={{ color: logColor, fontSize: '0.75rem' }}>{log}</div>
                              );
                            })}
                          </div>
                        </div>

                      </div>
                    ) : (
                      <span style={{ color: 'var(--text-muted)' }}>Run sandboxed tests above to inspect execution results console output logs.</span>
                    )}
                  </div>
                )}

              </div>
            </div>

          </div>

          {/* 3. Right Panel: Live UI Preview panel */}
          <div 
            style={{
              width: '360px',
              background: 'rgba(10, 13, 22, 0.2)',
              display: 'flex',
              flexDirection: 'column',
              height: '100%'
            }}
          >
            <div style={{ padding: '20px', borderBottom: '1px solid var(--border-color)', display: 'flex', alignItems: 'center', gap: '8px' }}>
              <Sparkles size={16} style={{ color: 'var(--color-info)' }} />
              <span style={{ fontSize: '0.8rem', fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.05em', color: 'var(--text-primary)' }}>Live UI Preview</span>
            </div>

            <div style={{ flex: 1, overflowY: 'auto', padding: '20px' }}>
              {yamlError ? (
                <div 
                  style={{
                    padding: '16px',
                    borderRadius: '8px',
                    background: 'rgba(239, 68, 68, 0.08)',
                    border: '1px solid var(--color-error)',
                    color: 'var(--text-primary)',
                    display: 'flex',
                    flexDirection: 'column',
                    gap: '10px',
                    boxShadow: '0 0 15px rgba(239, 68, 68, 0.1)'
                  }}
                >
                  <div style={{ display: 'flex', alignItems: 'center', gap: '8px', color: 'var(--color-error)', fontWeight: 700, fontSize: '0.85rem' }}>
                    <AlertCircle size={16} />
                    <span>YAML Parse Error</span>
                  </div>
                  <pre 
                    style={{ 
                      fontSize: '0.75rem', 
                      fontFamily: "'JetBrains Mono', monospace", 
                      whiteSpace: 'pre-wrap', 
                      wordBreak: 'break-all',
                      color: 'var(--text-secondary)'
                    }}
                  >
                    {yamlError}
                  </pre>
                </div>
              ) : parsedManifest ? (
                <div style={{ display: 'flex', flexDirection: 'column', gap: '20px' }}>
                  
                  {/* Mock node UI wrapper mimicking canvas panel design */}
                  <div 
                    style={{
                      padding: '16px',
                      borderRadius: '12px',
                      background: 'rgba(16, 22, 37, 0.8)',
                      border: '1px solid var(--border-color)',
                      boxShadow: '0 10px 25px -5px rgba(0, 0, 0, 0.5)'
                    }}
                  >
                    <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '12px' }}>
                      <div 
                        style={{
                          width: '10px',
                          height: '10px',
                          borderRadius: '50%',
                          background: parsedManifest.tier === 'compiled' ? 'var(--color-info)' : 'var(--color-success)'
                        }}
                      ></div>
                      <span style={{ fontSize: '0.75rem', fontWeight: 700, textTransform: 'uppercase', color: '#fff' }}>
                        {parsedManifest.displayName || 'Unnamed Node'}
                      </span>
                    </div>
                    <p style={{ fontSize: '0.75rem', color: 'var(--text-secondary)' }}>
                      Category: <strong style={{ color: '#fff' }}>{parsedManifest.category || 'Utility'}</strong>
                    </p>
                  </div>

                  {/* Render shared ManifestForm */}
                  <div style={{ padding: '4px' }}>
                    <ManifestForm
                      manifest={{
                        id: parsedManifest.id || 'node.package',
                        displayName: parsedManifest.displayName || 'Node Package',
                        parameters: parsedManifest.parameters || []
                      }}
                      properties={previewProperties}
                      onChange={setPreviewProperties}
                    />
                  </div>

                </div>
              ) : (
                <div style={{ display: 'flex', height: '100%', alignItems: 'center', justifyContent: 'center', color: 'var(--text-muted)' }}>
                  <span>Manifest preview will populate dynamically on active code changes.</span>
                </div>
              )}
            </div>
          </div>

        </div>

      </div>

      {/* 4. Wizard Creation Modal */}
      {showWizard && (
        <div 
          style={{
            position: 'fixed',
            top: 0,
            left: 0,
            width: '100vw',
            height: '100vh',
            background: 'rgba(0, 0, 0, 0.85)',
            backdropFilter: 'blur(8px)',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            zIndex: 1000
          }}
        >
          <div 
            style={{
              width: '460px',
              padding: '30px',
              background: 'var(--bg-surface-opaque)',
              border: '1px solid var(--border-color)',
              borderRadius: '16px',
              boxShadow: '0 20px 50px rgba(0, 0, 0, 0.8)',
              display: 'flex',
              flexDirection: 'column',
              gap: '20px'
            }}
          >
            <div>
              <h3 style={{ fontSize: '1.2rem', fontWeight: 700, color: '#fff', marginBottom: '6px' }}>Create Node Package</h3>
              <p style={{ fontSize: '0.8rem', color: 'var(--text-secondary)' }}>Bootstrap a declarative connector or compiled executor with built-in templates.</p>
            </div>

            <div style={{ display: 'flex', flexDirection: 'column', gap: '14px' }}>
              <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                <label style={{ fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)' }}>PACKAGE ID (lowercase dotted)</label>
                <input
                  type="text"
                  placeholder="e.g. system.alert"
                  value={newId}
                  onChange={(e) => setNewId(e.target.value)}
                  style={{
                    padding: '10px 12px',
                    borderRadius: '8px',
                    background: 'rgba(0,0,0,0.3)',
                    border: '1px solid var(--border-color)',
                    color: '#fff',
                    outline: 'none',
                    fontSize: '0.85rem'
                  }}
                />
              </div>

              <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                <label style={{ fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)' }}>DISPLAY NAME</label>
                <input
                  type="text"
                  placeholder="e.g. System Alert Trigger"
                  value={newDisplayName}
                  onChange={(e) => setNewDisplayName(e.target.value)}
                  style={{
                    padding: '10px 12px',
                    borderRadius: '8px',
                    background: 'rgba(0,0,0,0.3)',
                    border: '1px solid var(--border-color)',
                    color: '#fff',
                    outline: 'none',
                    fontSize: '0.85rem'
                  }}
                />
              </div>

              <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                <label style={{ fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)' }}>CATEGORY</label>
                <select
                  value={newCategory}
                  onChange={(e) => setNewCategory(e.target.value)}
                  style={{
                    padding: '10px 12px',
                    borderRadius: '8px',
                    background: '#101625',
                    border: '1px solid var(--border-color)',
                    color: '#fff',
                    outline: 'none',
                    fontSize: '0.85rem'
                  }}
                >
                  <option value="Utility">Utility</option>
                  <option value="Network">Network</option>
                  <option value="Data">Data</option>
                  <option value="Logic">Logic</option>
                  <option value="Trigger">Trigger</option>
                </select>
              </div>

              <div style={{ display: 'flex', flexDirection: 'column', gap: '6px' }}>
                <label style={{ fontSize: '0.75rem', fontWeight: 700, color: 'var(--text-secondary)' }}>TIER LEVEL</label>
                <div style={{ display: 'flex', gap: '10px' }}>
                  <button
                    onClick={() => setNewTier('declarative')}
                    style={{
                      flex: 1,
                      padding: '10px',
                      borderRadius: '8px',
                      background: newTier === 'declarative' ? 'rgba(16, 185, 129, 0.15)' : 'rgba(0,0,0,0.2)',
                      border: newTier === 'declarative' ? '1px solid var(--color-success)' : '1px solid var(--border-color)',
                      color: newTier === 'declarative' ? 'var(--color-success)' : 'var(--text-secondary)',
                      cursor: 'pointer',
                      fontWeight: 600,
                      fontSize: '0.8rem',
                      transition: 'all 0.2s'
                    }}
                  >
                    Declarative (Tier 1)
                  </button>
                  <button
                    onClick={() => setNewTier('compiled')}
                    style={{
                      flex: 1,
                      padding: '10px',
                      borderRadius: '8px',
                      background: newTier === 'compiled' ? 'rgba(6, 182, 212, 0.15)' : 'rgba(0,0,0,0.2)',
                      border: newTier === 'compiled' ? '1px solid var(--color-info)' : '1px solid var(--border-color)',
                      color: newTier === 'compiled' ? 'var(--color-info)' : 'var(--text-secondary)',
                      cursor: 'pointer',
                      fontWeight: 600,
                      fontSize: '0.8rem',
                      transition: 'all 0.2s'
                    }}
                  >
                    Compiled C# (Tier 2)
                  </button>
                </div>
              </div>
            </div>

            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: '10px', marginTop: '10px' }}>
              <button 
                onClick={() => setShowWizard(false)}
                style={{
                  padding: '10px 16px',
                  borderRadius: '8px',
                  background: 'transparent',
                  border: 'none',
                  color: 'var(--text-secondary)',
                  cursor: 'pointer',
                  fontSize: '0.85rem'
                }}
              >
                Cancel
              </button>
              <button 
                onClick={handleCreateNode}
                style={{
                  padding: '10px 20px',
                  borderRadius: '8px',
                  background: 'linear-gradient(135deg, var(--color-accent), #4f46e5)',
                  border: 'none',
                  color: '#fff',
                  cursor: 'pointer',
                  fontWeight: 600,
                  fontSize: '0.85rem',
                  boxShadow: '0 0 15px rgba(99, 102, 241, 0.3)'
                }}
              >
                Create package
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Embedded CSS Animations */}
      <style>{`
        .spinner {
          box-sizing: border-box;
        }
        @keyframes spin {
          0% { transform: rotate(0deg); }
          100% { transform: rotate(360deg); }
        }
        @keyframes fadeIn {
          from { opacity: 0; transform: translateY(-4px); }
          to { opacity: 1; transform: translateY(0); }
        }
        .package-item-hover:hover {
          background: rgba(255,255,255,0.02) !important;
        }
        .hover-glow:hover {
          box-shadow: 0 0 12px rgba(99,102,241,0.4);
          transform: translateY(-1px);
        }
      `}</style>
    </div>
  );
}
