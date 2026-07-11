// journal-panels.jsx — three redesigned "Runner Journal" panel directions.
// Shared run data + icon system, then PanelA/PanelB/PanelC. Exported to window.

const RUN = [
  {
    step: 1,
    name: "Cron Scheduler",
    id: "scheduler-ldr0aa924",
    type: "trigger",
    state: "completed",
    elapsed: "+0ms",
    summary: "Triggered · */5 * * * *",
    events: [
      { kind: "Completed", text: "Trigger node activated." },
    ],
    payload: { triggeredAt: "2026-05-31T15:57:13.6167+00:00" },
  },
  {
    step: 2,
    name: "Log",
    id: "log-xjvuunpkv",
    type: "log",
    state: "completed",
    elapsed: "+9ms",
    summary: "\u201clog message\u201d",
    events: [
      { kind: "Started", text: "Executing node (type \u2018log\u2019)." },
      { kind: "Completed", text: "[LOG] log message" },
    ],
    payload: { message: "log message" },
  },
  {
    step: 3,
    name: "End",
    id: "end-kj4v7xq3l",
    type: "end",
    state: "completed",
    elapsed: "+12ms",
    summary: "End execution",
    events: [
      { kind: "Started", text: "Executing node (type \u2018end\u2019)." },
      { kind: "Completed", text: "Completed successfully." },
    ],
    payload: null,
  },
];

// node type -> color + icon. This mapping is the BRIDGE between log and canvas.
const TYPE = {
  trigger: { color: "#22d3ee", glow: "34,211,238", label: "TRIGGER" },
  log: { color: "#3b9eff", glow: "59,158,255", label: "LOG" },
  end: { color: "#f0556d", glow: "240,85,109", label: "END" },
};

function TypeIcon({ type, size = 16 }) {
  const c = TYPE[type].color;
  if (type === "trigger") {
    return (
      <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke={c} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="12" cy="12" r="9" />
        <path d="M12 7v5l3 2" />
      </svg>
    );
  }
  if (type === "log") {
    return (
      <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke={c} strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z" />
        <path d="M14 3v5h5" /><path d="M9 13h6M9 17h4" />
      </svg>
    );
  }
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill={c}>
      <rect x="5" y="5" width="14" height="14" rx="3" />
    </svg>
  );
}

function Check({ c = "#34d399", size = 13 }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke={c} strokeWidth="3" strokeLinecap="round" strokeLinejoin="round">
      <path d="M5 13l4 4L19 7" />
    </svg>
  );
}

function StatePill({ state }) {
  return (
    <span className="jp-pill">
      <Check />
      <span>COMPLETED</span>
    </span>
  );
}

function PanelShell({ title, sub, children, accent = "#22d3ee" }) {
  return (
    <div className="jp-panel">
      <div className="jp-head">
        <span className="jp-head-icon" style={{ color: accent }}>{">_"}</span>
        <div>
          <div className="jp-head-title">{title}</div>
          <div className="jp-head-sub">{sub}</div>
        </div>
        <span className="jp-run-ok">
          <span className="jp-dot" /> Run OK · 21ms
        </span>
      </div>
      {children}
    </div>
  );
}

/* ============================ DIRECTION A ============================ */
/* Grouped: one collapsible row per node. Friendly name primary, id muted, */
/* state + elapsed on the right, expand for payload. */
function PanelA() {
  const [open, setOpen] = React.useState({ 2: true });
  return (
    <PanelShell title="RUN JOURNAL" sub="Grouped by node · 3 nodes">
      <div className="jp-list">
        {RUN.map((n) => {
          const t = TYPE[n.type];
          const isOpen = open[n.step];
          return (
            <div key={n.id} className="jpa-row" style={{ "--c": t.color, "--g": t.glow }}>
              <button className="jpa-main" onClick={() => setOpen((o) => ({ ...o, [n.step]: !o[n.step] }))}>
                <span className="jpa-idx">{n.step}</span>
                <span className="jpa-ico"><TypeIcon type={n.type} /></span>
                <span className="jpa-id">
                  <span className="jpa-name">{n.name}</span>
                  <span className="jpa-uid">{n.id}</span>
                </span>
                <span className="jpa-right">
                  <StatePill state={n.state} />
                  <span className="jpa-ms">{n.elapsed}</span>
                  <svg className={"jpa-chev" + (isOpen ? " open" : "")} width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#6b7785" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round"><path d="M6 9l6 6 6-6" /></svg>
                </span>
              </button>
              {isOpen && (
                <div className="jpa-body">
                  {n.events.map((e, i) => (
                    <div key={i} className="jpa-ev">
                      <span className={"jpa-evtag " + (e.kind === "Started" ? "started" : "done")}>{e.kind}</span>
                      <span className="jpa-evtxt">{e.text}</span>
                    </div>
                  ))}
                  {n.payload && (
                    <pre className="jpa-pay">{JSON.stringify(n.payload, null, 2)}</pre>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
      <div className="jp-foot ok">
        <Check c="#34d399" size={15} /> Workflow run completed successfully.
      </div>
    </PanelShell>
  );
}

/* ============================ DIRECTION B ============================ */
/* Vertical timeline / stepper that mirrors the canvas left-to-right order.  */
/* Rail of glowing node dots; sub-events nested faintly under each node.      */
function PanelB() {
  return (
    <PanelShell title="EXECUTION TIMELINE" sub="In run order · mirrors canvas">
      <div className="jpb-rail">
        {RUN.map((n, i) => {
          const t = TYPE[n.type];
          return (
            <div key={n.id} className="jpb-step" style={{ "--c": t.color, "--g": t.glow }}>
              <div className="jpb-gutter">
                <span className="jpb-node"><TypeIcon type={n.type} size={14} /></span>
                {i < RUN.length - 1 && <span className="jpb-line" />}
              </div>
              <div className="jpb-content">
                <div className="jpb-top">
                  <span className="jpb-name">{n.name}</span>
                  <span className="jpb-type">{t.label}</span>
                  <span className="jpb-ms">{n.elapsed}</span>
                </div>
                <div className="jpb-uid">{n.id}</div>
                <div className="jpb-events">
                  {n.events.map((e, j) => (
                    <div key={j} className="jpb-ev">
                      <span className={"jpb-evdot " + (e.kind === "Started" ? "s" : "d")} />
                      <span className="jpb-evtxt">{e.text}</span>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          );
        })}
      </div>
      <div className="jp-foot ok">
        <Check c="#34d399" size={15} /> Workflow run completed successfully.
      </div>
    </PanelShell>
  );
}

/* ============================ DIRECTION C ============================ */
/* Refined terminal stream: keeps the mono feel but fixes the noise.         */
/* Colored gutter per node, friendly name bold + muted id, elapsed deltas.   */
function PanelC() {
  const lines = [];
  RUN.forEach((n) => {
    n.events.forEach((e, i) =>
      lines.push({ node: n, ev: e, first: i === 0 })
    );
  });
  lines.push({ workflow: true });
  return (
    <PanelShell title="RUNNER STREAM" sub="Compact · elapsed deltas">
      <div className="jpc-stream">
        {lines.map((l, i) => {
          if (l.workflow) {
            return (
              <div key={i} className="jpc-line jpc-wf">
                <span className="jpc-gutter" style={{ background: "#34d399" }} />
                <span className="jpc-ms" style={{ color: "#34d399" }}>+21</span>
                <span className="jpc-tag wf">WORKFLOW</span>
                <span className="jpc-txt">Run completed successfully.</span>
              </div>
            );
          }
          const t = TYPE[l.node.type];
          return (
            <div key={i} className={"jpc-line" + (l.first ? " jpc-first" : "")}>
              <span className="jpc-gutter" style={{ background: t.color }} />
              <span className="jpc-ms">{l.first ? l.node.elapsed.replace("ms", "") : ""}</span>
              {l.first ? (
                <span className="jpc-node" style={{ color: t.color }}>
                  <TypeIcon type={l.node.type} size={12} />
                  <span className="jpc-name">{l.node.name}</span>
                  <span className="jpc-uid">{l.node.id}</span>
                </span>
              ) : (
                <span className="jpc-cont" />
              )}
              <span className={"jpc-tag " + (l.ev.kind === "Started" ? "s" : "d")}>{l.ev.kind === "Started" ? "RUN" : "OK"}</span>
              <span className="jpc-txt">{l.ev.text}</span>
            </div>
          );
        })}
      </div>
    </PanelShell>
  );
}

/* ===================== BEFORE (the current panel) ===================== */
function PanelBefore() {
  const raw = [
    { t: "NodeExecutionCompleted", c: "#34d399", m: "Trigger node 'scheduler-ldr0aa924' activated.", n: "scheduler-ldr0aa924", pay: true },
    { t: "NodeExecutionStarted", c: "#38bdf8", m: "Executing node 'log-xjvuunpkv' (type 'log').", n: "log-xjvuunpkv" },
    { t: "NodeExecutionCompleted", c: "#34d399", m: "[LOG] log message", n: "log-xjvuunpkv", pay: true },
    { t: "NodeExecutionStarted", c: "#38bdf8", m: "Executing node 'end-kj4v7xq3l' (type 'end').", n: "end-kj4v7xq3l" },
    { t: "NodeExecutionCompleted", c: "#34d399", m: "Node 'end-kj4v7xq3l' completed successfully.", n: "end-kj4v7xq3l" },
    { t: "WorkflowCompleted", c: "#34d399", m: "Workflow run completed successfully.", n: null },
  ];
  return (
    <div className="jp-panel">
      <div className="jp-head">
        <span className="jp-head-icon" style={{ color: "#f59e0b" }}>{">_"}</span>
        <div>
          <div className="jp-head-title">RUNNER JOURNAL STREAM</div>
          <div className="jp-head-sub">current</div>
        </div>
      </div>
      <div className="jpold-stream">
        {raw.map((r, i) => (
          <div key={i} className="jpold-line">
            <div className="jpold-top">
              <span className="jpold-ts">[17:57:13]</span>
              <span className="jpold-evt" style={{ color: r.c }}>{r.t}</span>
            </div>
            <div className="jpold-msg">{r.m}</div>
            {r.n && <div className="jpold-node">Node: {r.n}</div>}
            {r.pay && <div className="jpold-pay">{"\u25b6"} View payload</div>}
          </div>
        ))}
      </div>
    </div>
  );
}

Object.assign(window, { PanelA, PanelB, PanelC, PanelBefore });
