import { lazy, Suspense, useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { api } from '../../utils/api';
import type { InlineCodeTestResult } from '../../types';

const CodeEditor = lazy(() => import('@monaco-editor/react'));

// --- Knot Garden surface tokens (mirrors Editor Polished.html). ---
const C = {
  bg: '#0a0e16',        // deepest (output box / canvas)
  panel: '#0c111a',     // nested panel + code bg
  base: '#0f141d',      // modal base
  card: '#131922',      // lifted (close button)
  ink: '#e6edf3',
  muted: '#8593a6',
  faint: '#5d6675',
  faint2: '#444d5d',
  line: '#212b39',
  lineSoft: '#1b2430',
  green: '#34d399',
  violet: '#7c6cf0',
  violetL: '#a99bff',
};

// --- Level-B completion + a Knot-Garden Monaco theme so the editor bg matches the panels. ---
// Completion is NOT full Roslyn IntelliSense; it surfaces the helpers the wrapper injects and is
// context-aware (member access on State./context./Input.) plus knows the workflow's variable names.
let _completionsRegistered = false;
// The current workflow's variable names, fed in from ManifestForm so they can be autocompleted
// inside GetVariable("…") / SetVariable("…") / Input.Get("…").
let _inlineVarNames: string[] = [];
export function setInlineCodeVariableNames(names: string[]) { _inlineVarNames = names; }

export function registerCsharpInlineCompletions(monaco: any) {
  if (!monaco?.languages?.registerCompletionItemProvider) return;

  // Idempotent: align Monaco's surfaces with the panel palette (no near-black editor on lifted cards).
  try {
    monaco.editor.defineTheme('kg-dark', {
      base: 'vs-dark', inherit: true, rules: [],
      colors: {
        'editor.background': C.panel,
        'editorGutter.background': C.panel,
        'editor.lineHighlightBackground': '#101622',
        'editorLineNumber.foreground': C.faint2,
        'editorLineNumber.activeForeground': C.muted,
        'editor.selectionBackground': '#28324c',
        'editorWidget.background': C.base,
        'editorWidget.border': C.line,
        'editorSuggestWidget.background': C.base,
        'editorSuggestWidget.border': C.line,
      },
    });
  } catch { /* defineTheme unavailable in some test stubs */ }

  if (_completionsRegistered) return;
  _completionsRegistered = true;

  monaco.languages.registerCompletionItemProvider('csharp', {
    triggerCharacters: ['.', '"'],
    provideCompletionItems(model: any, position: any) {
      const k = monaco.languages.CompletionItemKind;
      const snip = monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet;
      const line: string = model.getValueInRange({
        startLineNumber: position.lineNumber, startColumn: 1,
        endLineNumber: position.lineNumber, endColumn: position.column,
      });
      const word = model.getWordUntilPosition(position);
      const range = {
        startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
        startColumn: word.startColumn, endColumn: word.endColumn,
      };

      // (a) Inside the string argument of a variable/input lookup → suggest known variable names.
      const strMatch = /(?:GetVariable|SetVariable|Input\.Get)\s*(?:<[^>]*>)?\s*\(\s*"([^"]*)$/.exec(line);
      if (strMatch) {
        const typed = strMatch[1];
        const strRange = {
          startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
          startColumn: position.column - typed.length, endColumn: position.column,
        };
        return {
          suggestions: _inlineVarNames.map(n => ({
            label: n, kind: k.Variable, detail: 'workflow variable', insertText: n, range: strRange,
          })),
        };
      }

      // (b) Member access — context-aware.
      if (/(?:^|[^\w.])(?:context\.)?State\.\w*$/.test(line)) {
        return { suggestions: [
          { label: 'GetVariable', kind: k.Method, detail: 'Read a global variable',
            documentation: 'GetVariable<T>("name") — typed read of a workflow variable.',
            insertText: 'GetVariable<${1:int}>("${2:name}")', insertTextRules: snip, range },
          { label: 'SetVariable', kind: k.Method, detail: 'Write a global variable',
            insertText: 'SetVariable("${1:name}", ${2:value})', insertTextRules: snip, range },
        ] };
      }
      if (/(?:^|[^\w.])context\.\w*$/.test(line)) {
        return { suggestions: [
          { label: 'State', kind: k.Property, detail: 'Workflow variables (GetVariable / SetVariable)', insertText: 'State', range },
          { label: 'Logger', kind: k.Property, detail: 'ILogger', insertText: 'Logger', range },
          { label: 'Http', kind: k.Property, detail: 'IHttpClient', insertText: 'Http', range },
          { label: 'Credentials', kind: k.Property, detail: 'ICredentialAccessor', insertText: 'Credentials', range },
        ] };
      }
      if (/(?:^|[^\w.])Input\.\w*$/.test(line)) {
        return { suggestions: [
          { label: 'Get', kind: k.Method, detail: 'Read an input by name',
            insertText: 'Get<${1:string}>("${2:name}")', insertTextRules: snip, range },
        ] };
      }

      // (c) Default: top-level helpers the wrapper injects.
      const suggestions = [
        { label: 'Input.Get', kind: k.Method, detail: 'Read an input/variable by name',
          documentation: 'Input.Get<T>(name) — deserialize a node input/sample variable.',
          insertText: 'Input.Get<${1:string}>("${2:name}")', insertTextRules: snip, range },
        { label: 'context.State.GetVariable', kind: k.Method, detail: 'Read a global variable',
          insertText: 'context.State.GetVariable<${1:int}>("${2:name}")', insertTextRules: snip, range },
        { label: 'context.State.SetVariable', kind: k.Method, detail: 'Write a global variable',
          insertText: 'context.State.SetVariable("${1:name}", ${2:value})', insertTextRules: snip, range },
        { label: 'Success', kind: k.Function, detail: 'Return success with an output payload',
          documentation: 'Whatever object you pass becomes the node\'s outputs.',
          insertText: 'Success(new { ${1:key} = ${2:value} })', insertTextRules: snip, range },
        { label: 'Fail', kind: k.Function, detail: 'Return a failure with a message',
          insertText: 'Fail("${1:error message}")', insertTextRules: snip, range },
        { label: 'Logger.LogInformation', kind: k.Method, detail: 'Log info (shown in the test panel)',
          insertText: 'Logger.LogInformation("${1:message}")', insertTextRules: snip, range },
        { label: 'Logger.LogWarning', kind: k.Method, detail: 'Log warning',
          insertText: 'Logger.LogWarning("${1:message}")', insertTextRules: snip, range },
        { label: 'Logger.LogError', kind: k.Method, detail: 'Log error',
          insertText: 'Logger.LogError("${1:message}")', insertTextRules: snip, range },
        { label: 'cancellationToken', kind: k.Variable, detail: 'Cooperative timeout token',
          documentation: 'Pass to awaited calls (e.g. Task.Delay) so the node timeout can interrupt them.',
          insertText: 'cancellationToken', range },
        { label: 'foreach', kind: k.Snippet, detail: 'foreach loop',
          insertText: 'foreach (var ${1:item} in ${2:items})\n{\n\t$0\n}', insertTextRules: snip, range },
        { label: 'if', kind: k.Snippet, detail: 'if block',
          insertText: 'if (${1:condition})\n{\n\t$0\n}', insertTextRules: snip, range },
      ];
      return { suggestions };
    },
  });
}

const editorOptions = {
  minimap: { enabled: false },
  fontSize: 13.5,
  lineNumbers: 'on' as const,
  scrollBeyondLastLine: false,
  tabSize: 4,
  automaticLayout: true,
  padding: { top: 10, bottom: 10 },
  overviewRulerLanes: 0,
  overviewRulerBorder: false,
  hideCursorInOverviewRuler: true,
  renderLineHighlight: 'none' as const,
  glyphMargin: false,
  folding: false,
  lineNumbersMinChars: 3,
  scrollbar: { useShadows: false, verticalScrollbarSize: 10, horizontalScrollbarSize: 10 },
};

// Ready-to-run snippets for the actual inline-code API surface. Inserted at the cursor.
const SAMPLES: { label: string; code: string }[] = [
  { label: 'Return a value',
    code: 'return Success(new { message = "Hello", at = DateTimeOffset.UtcNow.ToString("o") });' },
  { label: 'Read an input',
    code: 'var name = Input.Get<string>("name") ?? "world";\nreturn Success(new { greeting = $"Hello, {name}!" });' },
  { label: 'Log a message',
    code: 'Logger.LogInformation("Inline code ran at {Time}", DateTimeOffset.UtcNow);\nreturn Success();' },
  { label: 'Read & write a global variable',
    code: 'var count = context.State.GetVariable<int>("count");\ncount++;\ncontext.State.SetVariable("count", count);\nreturn Success(new { count });' },
  { label: 'Transform a list',
    code: 'var items = Input.Get<List<int>>("items") ?? new List<int>();\nvar doubled = items.Select(x => x * 2).ToList();\nreturn Success(new { doubled, total = doubled.Sum() });' },
  { label: 'Fail on a condition',
    code: 'var value = Input.Get<int>("value");\nif (value < 0)\n    return Fail("value must be >= 0");\nreturn Success(new { value });' },
  { label: 'Parse JSON input',
    code: 'var raw = Input.Get<string>("payload") ?? "{}";\nusing var doc = JsonDocument.Parse(raw);\nvar keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();\nreturn Success(new { keys });' },
];

const ANIM_MS = 200;
const EASING = 'cubic-bezier(0.2, 0.8, 0.2, 1)';

function prefersReducedMotion(): boolean {
  return typeof window !== 'undefined'
    && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
}

// Center of the canvas (.react-flow pane) in viewport coords — so the modal never slides
// under the properties rail. Falls back to viewport center if the pane isn't found.
function canvasCenter(): { x: number; y: number } {
  const rf = document.querySelector('.react-flow');
  const r = rf?.getBoundingClientRect();
  if (r && r.width > 0 && r.height > 0) {
    return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
  }
  return { x: window.innerWidth / 2, y: window.innerHeight / 2 };
}

interface InlineCodeEditorModalProps {
  open: boolean;
  code: string;
  language: string;
  nodeId?: string | null;
  // outputKeys: the field names the script returned (from a passing run), so they can be
  // surfaced as draggable output chips on the node.
  onSave: (code: string, meta?: { outputKeys?: string[] }) => void;
  onClose: () => void;
}

export function InlineCodeEditorModal({ open, code, language, nodeId, onSave, onClose }: InlineCodeEditorModalProps) {
  const [buffer, setBuffer] = useState(code);
  const [savedCode, setSavedCode] = useState(code);
  const [sampleInputs, setSampleInputs] = useState<string>('{\n  \n}');
  const [savedInputs, setSavedInputs] = useState<string>('{\n  \n}');
  const [result, setResult] = useState<InlineCodeTestResult | null>(null);
  const [running, setRunning] = useState(false);
  const [localError, setLocalError] = useState<string | null>(null);
  const [showConfirm, setShowConfirm] = useState(false);
  const [showSamples, setShowSamples] = useState(false);
  const [center, setCenter] = useState<{ x: number; y: number }>({ x: 0, y: 0 });

  const modalRef = useRef<HTMLDivElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const editorRef = useRef<any>(null);

  // Insert a sample at the cursor (or replace the selection). Falls back to appending if the
  // editor instance isn't ready yet.
  const insertSample = (snippet: string) => {
    setShowSamples(false);
    const ed = editorRef.current;
    if (ed) {
      const sel = ed.getSelection();
      ed.executeEdits('inline-samples', [{ range: sel, text: snippet, forceMoveMarkers: true }]);
      ed.focus();
    } else {
      setBuffer(prev => (prev && prev.trim() ? prev + '\n\n' + snippet : snippet));
    }
  };

  const dirty = buffer !== savedCode || sampleInputs !== savedInputs;

  // Keep keystrokes inside the editor: the canvas (React Flow) has document-level key
  // listeners — Space activates panning (swallowing the spacebar), Delete/Backspace delete
  // the selected node. Stop keydown bubbling out of the modal so typing works normally.
  // (Esc still closes: that listener runs in the capture phase, before this bubble handler.)
  useEffect(() => {
    if (!open) return;
    const el = containerRef.current;
    if (!el) return;
    const stop = (e: KeyboardEvent) => { e.stopPropagation(); };
    el.addEventListener('keydown', stop);
    return () => el.removeEventListener('keydown', stop);
  }, [open]);

  // Seed buffers + baseline when the modal opens.
  useEffect(() => {
    if (open) {
      setBuffer(code);
      setSavedCode(code);
      setResult(null);
      setLocalError(null);
      setShowConfirm(false);
      setShowSamples(false);
      setCenter(canvasCenter());
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  // Keep centered over the canvas on window resize.
  useEffect(() => {
    if (!open) return;
    const onResize = () => setCenter(canvasCenter());
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, [open]);

  // Grow-from-node open animation.
  useLayoutEffect(() => {
    if (!open) return;
    const el = modalRef.current;
    if (!el) return;

    if (prefersReducedMotion()) {
      el.style.transformOrigin = 'center center';
      el.style.transform = 'translate(-50%, -50%) scale(1)';
      el.style.opacity = '1';
      return;
    }

    // Measure final (scale 1) box to map the node center into the modal's own coordinate space.
    el.style.transition = 'none';
    el.style.transformOrigin = 'center center';
    el.style.transform = 'translate(-50%, -50%) scale(1)';
    el.style.opacity = '0';
    const m = el.getBoundingClientRect();

    const nodeEl = nodeId ? document.querySelector(`.react-flow__node[data-id="${nodeId}"]`) : null;
    if (nodeEl) {
      const n = nodeEl.getBoundingClientRect();
      const ox = ((n.left + n.width / 2 - m.left) / m.width) * 100;
      const oy = ((n.top + n.height / 2 - m.top) / m.height) * 100;
      el.style.transformOrigin = `${ox}% ${oy}%`;
    }

    // Start small, then animate to full size on the next frame.
    el.style.transform = 'translate(-50%, -50%) scale(0.6)';
    void el.offsetWidth; // force reflow so the transition picks up the change
    requestAnimationFrame(() => {
      el.style.transition = `transform ${ANIM_MS}ms ${EASING}, opacity ${ANIM_MS}ms ${EASING}`;
      el.style.transform = 'translate(-50%, -50%) scale(1)';
      el.style.opacity = '1';
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, center]);

  const closeNow = useCallback(() => {
    const el = modalRef.current;
    if (!el || prefersReducedMotion()) {
      onClose();
      return;
    }
    el.style.transition = `transform ${ANIM_MS}ms ${EASING}, opacity ${ANIM_MS}ms ${EASING}`;
    el.style.transform = 'translate(-50%, -50%) scale(0.6)';
    el.style.opacity = '0';
    window.setTimeout(() => onClose(), ANIM_MS);
  }, [onClose]);

  const runTest = useCallback(async (): Promise<boolean> => {
    setLocalError(null);
    setResult(null);

    let inputs: Record<string, unknown> = {};
    const trimmed = sampleInputs.trim();
    if (trimmed.length > 0) {
      try {
        const parsed = JSON.parse(trimmed);
        if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
          inputs = parsed as Record<string, unknown>;
        } else {
          setLocalError('Sample inputs must be a JSON object.');
          return false;
        }
      } catch {
        setLocalError('Sample inputs is not valid JSON.');
        return false;
      }
    }

    setRunning(true);
    try {
      const res = await api.testInlineCode(buffer, language || 'csharp', inputs);
      setResult(res);
      if (res.success) {
        // A passing run commits: save the code, capture the output field names (so they
        // become draggable chips downstream), and reset the dirty baseline.
        const outputKeys = res.output && typeof res.output === 'object' && !Array.isArray(res.output)
          ? Object.keys(res.output as Record<string, unknown>)
          : [];
        onSave(buffer, { outputKeys });
        setSavedCode(buffer);
        setSavedInputs(sampleInputs);
      }
      return res.success;
    } catch (err) {
      setLocalError(err instanceof Error ? err.message : 'Test run failed.');
      return false;
    } finally {
      setRunning(false);
    }
  }, [buffer, language, sampleInputs, onSave]);

  const requestClose = useCallback(() => {
    if (!dirty) closeNow();
    else setShowConfirm(true);
  }, [dirty, closeNow]);

  // Esc closes (via the same path as scrim/Close).
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        if (showConfirm) setShowConfirm(false);
        else requestClose();
      }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [open, showConfirm, requestClose]);

  if (!open) return null;

  const editorLang = (language || 'csharp').toLowerCase().replace('c#', 'csharp');

  const confirmTestAndSave = async () => {
    const ok = await runTest();
    if (ok) closeNow();
    else setShowConfirm(false); // failed → keep editor open, error already shown, stays dirty
  };

  return createPortal(
    <div
      ref={containerRef}
      onMouseDown={(e) => { if (e.target === e.currentTarget) requestClose(); }}
      style={{
        position: 'fixed', inset: 0, zIndex: 1000,
        background: 'rgba(6, 9, 14, 0.6)', backdropFilter: 'blur(2px)',
        animation: prefersReducedMotion() ? undefined : `kg-scrim-in ${ANIM_MS}ms ${EASING}`,
      }}
    >
      <div
        ref={modalRef}
        style={{
          position: 'fixed',
          left: center.x, top: center.y,
          transform: 'translate(-50%, -50%) scale(0.6)',
          opacity: 0,
          width: 'min(1060px, 88vw)', height: 'min(660px, 86vh)',
          display: 'flex', flexDirection: 'column',
          background: C.base,
          border: `1px solid ${C.line}`, borderRadius: '18px',
          overflow: 'hidden',
          boxShadow: '0 60px 140px -40px rgba(0,0,0,0.95), 0 0 0 1px rgba(124,108,240,0.05)',
          color: C.ink,
          fontFamily: '"Inter", system-ui, -apple-system, "Segoe UI", sans-serif',
        }}
      >
        {/* Header */}
        <div style={{ display: 'flex', alignItems: 'center', gap: '13px', padding: '16px 18px', flex: 'none' }}>
          <span style={{
            width: '30px', height: '30px', borderRadius: '9px', display: 'grid', placeItems: 'center', flex: 'none',
            background: 'rgba(124,108,240,0.14)', border: '1px solid rgba(124,108,240,0.3)', color: C.violetL,
            fontFamily: 'ui-monospace, Menlo, monospace', fontSize: '13px',
          }}>&lt;/&gt;</span>
          <span style={{ fontSize: '16px', fontWeight: 750, letterSpacing: '-0.01em' }}>
            Inline Code Editor <span style={{ color: C.faint, fontWeight: 600, fontSize: '14px', marginLeft: '7px' }}>{editorLang}</span>
            {dirty && <span style={{ color: '#e6a96b', marginLeft: '10px', fontSize: '12px', fontWeight: 700 }}>● unsaved</span>}
          </span>
          <span style={{ flex: 1 }} />
          <div style={{ position: 'relative' }}>
            <button onClick={() => setShowSamples(s => !s)} style={closeBtn} title="Insert a ready-made snippet at the cursor">
              Samples ▾
            </button>
            {showSamples && (
              <>
                <div onClick={() => setShowSamples(false)} style={{ position: 'fixed', inset: 0, zIndex: 5 }} />
                <div style={{
                  position: 'absolute', top: 'calc(100% + 6px)', right: 0, zIndex: 6, width: '260px',
                  background: C.base, border: `1px solid ${C.line}`, borderRadius: '10px',
                  boxShadow: '0 20px 50px -20px rgba(0,0,0,0.8)', padding: '6px', overflow: 'hidden',
                }}>
                  {SAMPLES.map(s => (
                    <button
                      key={s.label}
                      onClick={() => insertSample(s.code)}
                      style={{
                        display: 'block', width: '100%', textAlign: 'left', background: 'transparent',
                        border: 'none', color: C.ink, fontFamily: 'inherit', fontSize: '12.5px',
                        padding: '8px 10px', borderRadius: '7px', cursor: 'pointer',
                      }}
                      onMouseEnter={(e) => (e.currentTarget.style.background = C.card)}
                      onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
                    >
                      {s.label}
                    </button>
                  ))}
                </div>
              </>
            )}
          </div>
          <button onClick={() => void runTest()} disabled={running} style={runBtn(running)}>
            {running ? 'Running…' : '▶ Run test'}
          </button>
          <button onClick={requestClose} style={closeBtn}>Close</button>
        </div>

        {/* Body: panels float on the modal base with gaps, no hard rules */}
        <div style={{ flex: 1, minHeight: 0, display: 'flex', gap: '12px', padding: '4px 14px 14px' }}>
          {/* CODE */}
          <div style={{ display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0, flex: 1.45 }}>
            <div style={{ ...panel, flex: 1 }}>
              <PanelLabel>Code</PanelLabel>
              <div style={{ flex: 1, minHeight: 0 }}>
                <Suspense fallback={<Loading />}>
                  <CodeEditor
                    height="100%"
                    language={editorLang}
                    theme="kg-dark"
                    value={buffer}
                    onChange={(val) => setBuffer(val ?? '')}
                    beforeMount={registerCsharpInlineCompletions}
                    onMount={(ed) => { editorRef.current = ed; }}
                    options={editorOptions}
                  />
                </Suspense>
              </div>
              <div style={{ fontSize: '11.5px', color: C.faint, padding: '10px 15px', borderTop: `1px solid ${C.lineSoft}`, flex: 'none' }}>
                Helpers: <code style={codeTag}>Input.Get&lt;T&gt;(name)</code>, <code style={codeTag}>Logger</code>, <code style={codeTag}>Success(obj)</code>, <code style={codeTag}>Fail(msg)</code>, <code style={codeTag}>cancellationToken</code>
              </div>
            </div>
          </div>

          {/* TEST */}
          <div style={{ display: 'flex', flexDirection: 'column', minWidth: '320px', minHeight: 0, flex: 1, gap: '12px' }}>
            <div style={{ ...panel, flex: 'none', height: '168px' }}>
              <PanelLabel>Sample inputs <span style={{ color: C.violetL, fontSize: '10.5px', textTransform: 'none', letterSpacing: 0, fontWeight: 600, fontFamily: 'ui-monospace, Menlo, monospace' }}>— readable via Input.Get&lt;T&gt;(name)</span></PanelLabel>
              <div style={{ flex: 1, minHeight: 0 }}>
                <Suspense fallback={<Loading />}>
                  <CodeEditor
                    height="100%"
                    language="json"
                    theme="kg-dark"
                    value={sampleInputs}
                    onChange={(val) => setSampleInputs(val ?? '')}
                    options={{ ...editorOptions, lineNumbers: 'off', lineNumbersMinChars: 0 }}
                  />
                </Suspense>
              </div>
            </div>

            <div style={{ ...panel, flex: 1 }}>
              <PanelLabel>Result</PanelLabel>
              <div style={{ flex: 1, minHeight: 0, overflow: 'auto', padding: '4px 16px 16px', fontSize: '0.8rem' }}>
                {localError && <Block title="Error" color="#f87171">{localError}</Block>}
                {!localError && !result && (
                  <div style={{ color: C.faint, fontSize: '13px', lineHeight: 1.55, paddingTop: '6px' }}>
                    Run the script to see its output, logs, and any errors. A passing run saves automatically.
                  </div>
                )}
                {result && (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '14px' }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: '9px', margin: '2px 0' }}>
                      <span style={badge(result.success)}>
                        {result.success ? '✓ Success' : '✗ Failed'}
                        {result.success && <span style={{ opacity: 0.75, fontWeight: 600 }}>(saved)</span>}
                      </span>
                      <span style={{ fontSize: '12px', color: C.faint, fontFamily: 'ui-monospace, Menlo, monospace' }}>· {result.elapsedMs} ms</span>
                    </div>
                    {result.error && <Block title="Error" color="#f87171">{result.error}</Block>}
                    {result.success && result.output !== undefined && result.output !== null && (
                      <Block title="Output">{JSON.stringify(result.output, null, 2)}</Block>
                    )}
                    {result.logs && result.logs.length > 0 && (
                      <Block title="Logs">{result.logs.join('\n')}</Block>
                    )}
                  </div>
                )}
              </div>
            </div>
          </div>
        </div>

        {/* Confirm-on-close */}
        {showConfirm && (
          <div style={{ position: 'absolute', inset: 0, background: 'rgba(6,9,14,0.6)', backdropFilter: 'blur(2px)', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
            <div style={{ width: 'min(420px, 80%)', background: C.base, border: `1px solid ${C.line}`, borderRadius: '14px', padding: '20px 22px', boxShadow: '0 30px 70px -30px rgba(0,0,0,0.9)' }}>
              <div style={{ fontWeight: 750, color: C.ink, marginBottom: '6px', fontSize: '15px' }}>Unsaved changes</div>
              <div style={{ fontSize: '0.85rem', color: C.muted, marginBottom: '18px', lineHeight: 1.5 }}>
                Test &amp; save before closing? A failing test will keep the editor open.
              </div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
                <button onClick={() => void confirmTestAndSave()} disabled={running} style={runBtn(running)}>
                  {running ? 'Testing…' : 'Test & Save'}
                </button>
                <button onClick={closeNow} style={closeBtn}>Leave without saving</button>
                <button onClick={() => setShowConfirm(false)} style={closeBtn}>Cancel</button>
              </div>
            </div>
          </div>
        )}
      </div>

      <style>{`@keyframes kg-scrim-in { from { opacity: 0 } to { opacity: 1 } }`}</style>
    </div>,
    document.body
  );
}

function Loading() {
  return <div style={{ padding: '12px', fontSize: '0.8rem', color: C.faint }}>Loading editor…</div>;
}

function PanelLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{ fontSize: '10.5px', fontWeight: 800, letterSpacing: '0.07em', textTransform: 'uppercase', color: C.muted, padding: '12px 15px 9px', flex: 'none' }}>
      {children}
    </div>
  );
}

function Block({ title, color, children }: { title: string; color?: string; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '8px' }}>
      <span style={{ fontSize: '10.5px', fontWeight: 800, letterSpacing: '0.07em', textTransform: 'uppercase', color: color ?? C.muted }}>{title}</span>
      <pre style={{ margin: 0, padding: '11px 13px', background: C.bg, border: `1px solid ${C.lineSoft}`, borderRadius: '9px', fontFamily: 'ui-monospace, Menlo, monospace', fontSize: '13px', color: color ?? '#c4ccd8', whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{children}</pre>
    </div>
  );
}

const panel: React.CSSProperties = {
  background: C.panel, border: `1px solid ${C.line}`, borderRadius: '13px',
  display: 'flex', flexDirection: 'column', minHeight: 0, overflow: 'hidden',
};

const codeTag: React.CSSProperties = { color: C.muted, fontFamily: 'ui-monospace, Menlo, monospace' };

const runBtn = (disabled: boolean): React.CSSProperties => ({
  display: 'inline-flex', alignItems: 'center', gap: '8px',
  background: C.violet, color: '#fff', border: 'none', fontFamily: 'inherit',
  fontSize: '13px', fontWeight: 700, borderRadius: '10px', padding: '10px 17px',
  cursor: disabled ? 'default' : 'pointer', opacity: disabled ? 0.6 : 1,
  boxShadow: '0 8px 20px -8px rgba(124,108,240,0.7)',
});

const closeBtn: React.CSSProperties = {
  background: C.card, border: `1px solid ${C.line}`, color: C.ink, fontFamily: 'inherit',
  fontSize: '13px', fontWeight: 600, borderRadius: '10px', padding: '10px 17px', cursor: 'pointer',
};

const badge = (success: boolean): React.CSSProperties => ({
  display: 'inline-flex', alignItems: 'center', gap: '6px', fontSize: '12.5px', fontWeight: 700,
  color: success ? C.green : '#f87171',
  background: success ? 'rgba(52,211,153,0.12)' : 'rgba(248,113,113,0.12)',
  border: `1px solid ${success ? 'rgba(52,211,153,0.3)' : 'rgba(248,113,113,0.3)'}`,
  borderRadius: '7px', padding: '4px 10px',
});
