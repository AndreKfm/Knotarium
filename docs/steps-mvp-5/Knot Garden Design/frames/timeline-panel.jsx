// timeline-panel.jsx — merged direction: connected timeline (B) + expandable
// per-node details (A). One component, driven by run data, handling every
// node state and both trigger modes. Exported to window as TimelinePanel + RUNS.

/* ----------------------------- icon system ----------------------------- */
const NTYPE = {
  trigger:   { color: "#22d3ee", glow: "34,211,238" },
  log:       { color: "#3b9eff", glow: "59,158,255" },
  http:      { color: "#a78bfa", glow: "167,139,250" },
  transform: { color: "#f0b429", glow: "240,180,41" },
  end:       { color: "#f0556d", glow: "240,85,109" },
};

function TypeIcon({ type, size = 15, color }) {
  const c = color || NTYPE[type].color;
  const p = { width: size, height: size, viewBox: "0 0 24 24", fill: "none", stroke: c, strokeWidth: 2, strokeLinecap: "round", strokeLinejoin: "round" };
  switch (type) {
    case "trigger": return <svg {...p}><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></svg>;
    case "log": return <svg {...p}><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z" /><path d="M14 3v5h5" /><path d="M9 13h6M9 17h4" /></svg>;
    case "http": return <svg {...p}><circle cx="12" cy="12" r="9" /><path d="M3 12h18M12 3c2.5 2.5 2.5 15 0 18M12 3c-2.5 2.5-2.5 15 0 18" /></svg>;
    case "transform": return <svg {...p}><path d="M8 6l-5 6 5 6M16 6l5 6-5 6" /></svg>;
    case "end": return <svg width={size} height={size} viewBox="0 0 24 24" fill={c}><rect x="5" y="5" width="14" height="14" rx="3" /></svg>;
    default: return null;
  }
}

/* ------------------------- node state visuals ------------------------- */
// completed | running | pending | failed | skipped
const STATE = {
  completed: { ring: "#34d399", glow: "52,211,153", pill: "COMPLETED", pc: "#4ade9f", pbg: "rgba(52,211,153,0.1)", pbd: "rgba(52,211,153,0.28)" },
  running:   { ring: "#22d3ee", glow: "34,211,238", pill: "RUNNING",   pc: "#5fd9f0", pbg: "rgba(34,211,238,0.1)",  pbd: "rgba(34,211,238,0.3)" },
  pending:   { ring: "#5a6675", glow: "90,102,117", pill: "PENDING",   pc: "#8b97a7", pbg: "rgba(120,135,155,0.07)", pbd: "rgba(120,135,155,0.2)" },
  failed:    { ring: "#f0556d", glow: "240,85,109", pill: "FAILED",    pc: "#ff8497", pbg: "rgba(240,85,109,0.1)",  pbd: "rgba(240,85,109,0.32)" },
  skipped:   { ring: "#475063", glow: "71,80,99",   pill: "SKIPPED",   pc: "#737f8f", pbg: "rgba(120,135,155,0.05)", pbd: "rgba(120,135,155,0.16)" },
};

function StateMark({ state }) {
  if (state === "completed") return <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="#0a0e15" strokeWidth="3.4" strokeLinecap="round" strokeLinejoin="round"><path d="M5 13l4 4L19 7" /></svg>;
  if (state === "failed") return <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="#0a0e15" strokeWidth="3.4" strokeLinecap="round"><path d="M6 6l12 12M18 6L6 18" /></svg>;
  if (state === "running") return <span className="tp-spin" />;
  if (state === "pending") return <span className="tp-hollow" />;
  return <span className="tp-skip-dash" />;
}

function StatePill({ state }) {
  const s = STATE[state];
  return <span className="tp-pill" style={{ color: s.pc, background: s.pbg, borderColor: s.pbd }}>{s.pill}</span>;
}

/* --------------------------- the panel --------------------------- */
function TimelinePanel({ run }) {
  const [open, setOpen] = React.useState(() => {
    const init = {};
    (run.openByDefault || []).forEach((id) => (init[id] = true));
    return init;
  });
  const toggle = (id) => setOpen((o) => ({ ...o, [id]: !o[id] }));

  const head = STATE[run.status] || STATE.completed;
  const trig = run.trigger;

  return (
    <div className="tp-panel">
      {/* header */}
      <div className="tp-head">
        <div className="tp-head-l">
          <span className="tp-head-icon">{">_"}</span>
          <div>
            <div className="tp-head-title">EXECUTION TIMELINE</div>
            <div className="tp-head-sub">{run.nodes.length} nodes · {run.duration}</div>
          </div>
        </div>
        <span className="tp-status" style={{ color: head.pc, background: head.pbg, borderColor: head.pbd }}>
          {run.status === "running" && <span className="tp-status-pulse" style={{ background: head.ring }} />}
          {run.status === "running" ? "RUNNING" : run.status === "failed" ? "FAILED" : "SUCCESS"}
        </span>
      </div>

      {/* trigger reason banner — differentiates AUTO vs MANUAL */}
      <div className={"tp-trigbar " + trig.mode}>
        <span className="tp-trigbadge">
          {trig.mode === "auto" ? (
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7M21 4v4h-4" /></svg>
          ) : (
            <svg width="11" height="11" viewBox="0 0 24 24" fill="currentColor"><path d="M7 5l12 7-12 7z" /></svg>
          )}
          {trig.mode === "auto" ? "AUTO" : "MANUAL"}
        </span>
        <span className="tp-trigtxt">{trig.text}</span>
      </div>

      {/* rail */}
      <div className="tp-rail">
        {run.nodes.map((n, i) => {
          const t = NTYPE[n.type];
          const s = STATE[n.state];
          const isOpen = open[n.id];
          const next = run.nodes[i + 1];
          const isLast = i === run.nodes.length - 1;
          // connector segment state
          let seg = "pending";
          if (!isLast) {
            if (n.state === "completed" && next.state === "completed") seg = "done";
            else if (n.state === "completed" && next.state === "running") seg = "active";
            else if (n.state === "failed" || n.state === "skipped" || next.state === "skipped") seg = "dead";
            else seg = "pending";
          }
          const faded = n.state === "skipped";
          return (
            <div key={n.id} className={"tp-step" + (faded ? " faded" : "")} style={{ "--c": t.color, "--g": t.glow, "--sg": s.glow }}>
              <div className="tp-gutter">
                <span className={"tp-disc s-" + n.state}>
                  <span className="tp-disc-ico"><TypeIcon type={n.type} size={14} color={n.state === "skipped" ? "#6b7785" : t.color} /></span>
                  <span className={"tp-badge b-" + n.state}><StateMark state={n.state} /></span>
                </span>
                {!isLast && <span className={"tp-line seg-" + seg} />}
              </div>

              <div className="tp-body">
                <button className="tp-row" onClick={() => toggle(n.id)}>
                  <span className="tp-row-main">
                    <span className="tp-name">{n.name}</span>
                    <span className="tp-uid">{n.id}</span>
                  </span>
                  <span className="tp-row-right">
                    <StatePill state={n.state} />
                    {n.elapsed && <span className="tp-ms">{n.elapsed}</span>}
                    {n.events && n.events.length > 0 && (
                      <svg className={"tp-chev" + (isOpen ? " open" : "")} width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="#6b7785" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round"><path d="M6 9l6 6 6-6" /></svg>
                    )}
                  </span>
                </button>

                {n.summary && !isOpen && <div className="tp-summary">{n.summary}</div>}

                {isOpen && (
                  <div className="tp-detail">
                    {n.events.map((e, j) => (
                      <div key={j} className="tp-ev">
                        <span className={"tp-evdot " + e.kind} />
                        <span className="tp-evts">{e.ts}</span>
                        <span className="tp-evtxt">{e.text}</span>
                      </div>
                    ))}
                    {n.detail && (
                      <div className={"tp-block " + (n.detail.tone || "")}>
                        <div className="tp-block-label">{n.detail.label}</div>
                        <pre className="tp-block-body">{n.detail.body}</pre>
                      </div>
                    )}
                  </div>
                )}
              </div>
            </div>
          );
        })}
      </div>

      {/* footer */}
      <div className={"tp-foot " + run.status}>
        {run.status === "running" ? <span className="tp-foot-spin" /> : run.status === "failed" ? <span className="tp-foot-x">!</span> : <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#6ee7b7" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round"><path d="M5 13l4 4L19 7" /></svg>}
        <span>{run.footer}</span>
      </div>
    </div>
  );
}

/* ------------------------------ run data ------------------------------ */
const RUNS = {
  manual: {
    status: "completed",
    duration: "21ms",
    trigger: { mode: "manual", text: "Fired manually \u2014 \u201cFire now\u201d \u00b7 17:57:13" },
    footer: "Workflow run completed successfully.",
    openByDefault: ["log-xjvuunpkv"],
    nodes: [
      { name: "Cron Scheduler", id: "scheduler-ldr0aa924", type: "trigger", state: "completed", elapsed: "+0ms", summary: "Schedule */5 * * * * \u00b7 Europe/Berlin",
        events: [{ kind: "done", ts: "00.000", text: "Trigger node activated." }],
        detail: { label: "TRIGGERED AT", body: "2026-05-31T15:57:13.6167+00:00" } },
      { name: "Log", id: "log-xjvuunpkv", type: "log", state: "completed", elapsed: "+9ms", summary: "\u201clog message\u201d",
        events: [{ kind: "start", ts: "00.004", text: "Executing node (type \u2018log\u2019)." }, { kind: "done", ts: "00.009", text: "Emitted log line." }],
        detail: { label: "MESSAGE", tone: "log", body: "log message" } },
      { name: "End", id: "end-kj4v7xq3l", type: "end", state: "completed", elapsed: "+12ms",
        events: [{ kind: "start", ts: "00.010", text: "Executing node (type \u2018end\u2019)." }, { kind: "done", ts: "00.012", text: "Completed successfully." }] },
    ],
  },

  live: {
    status: "running",
    duration: "1.4s \u00b7 live",
    trigger: { mode: "auto", text: "Triggered by schedule \u00b7 */5 * * * * \u00b7 next 18:00:00" },
    footer: "Workflow running\u2026 2 of 5 nodes remaining.",
    openByDefault: ["log-9f2"],
    nodes: [
      { name: "Cron Scheduler", id: "scheduler-ldr0aa924", type: "trigger", state: "completed", elapsed: "+0ms", summary: "Auto fire \u00b7 */5 * * * *",
        events: [{ kind: "done", ts: "00.000", text: "Trigger node activated by scheduler." }],
        detail: { label: "TRIGGERED AT", body: "2026-05-31T15:55:00.0021+00:00" } },
      { name: "HTTP Request", id: "http-a17be3", type: "http", state: "completed", elapsed: "+842ms", summary: "GET /api/orders \u2192 200",
        events: [{ kind: "start", ts: "00.012", text: "Executing node (type \u2018http\u2019)." }, { kind: "done", ts: "00.854", text: "200 OK \u00b7 18 records." }],
        detail: { label: "RESPONSE", body: "{\n  \"status\": 200,\n  \"count\": 18\n}" } },
      { name: "Transform", id: "transform-c4", type: "transform", state: "completed", elapsed: "+1.1s", summary: "Mapped 18 \u2192 18 items",
        events: [{ kind: "start", ts: "00.860", text: "Executing node (type \u2018transform\u2019)." }, { kind: "done", ts: "01.103", text: "Mapping complete." }] },
      { name: "Log", id: "log-9f2", type: "log", state: "running", elapsed: "\u2026", summary: "writing 18 lines\u2026",
        events: [{ kind: "start", ts: "01.110", text: "Executing node (type \u2018log\u2019)\u2026" }],
        detail: { label: "MESSAGE", tone: "log", body: "Processed 18 orders for 2026-05-31" } },
      { name: "End", id: "end-kj4v7xq3l", type: "end", state: "pending" },
    ],
  },

  failed: {
    status: "failed",
    duration: "904ms",
    trigger: { mode: "auto", text: "Triggered by schedule \u00b7 */5 * * * * \u00b7 18:05:00" },
    footer: "Workflow failed at \u2018HTTP Request\u2019. 2 nodes skipped.",
    openByDefault: ["http-a17be3"],
    nodes: [
      { name: "Cron Scheduler", id: "scheduler-ldr0aa924", type: "trigger", state: "completed", elapsed: "+0ms", summary: "Auto fire \u00b7 */5 * * * *",
        events: [{ kind: "done", ts: "00.000", text: "Trigger node activated by scheduler." }] },
      { name: "HTTP Request", id: "http-a17be3", type: "http", state: "failed", elapsed: "+901ms", summary: "GET /api/orders \u2192 503",
        events: [{ kind: "start", ts: "00.011", text: "Executing node (type \u2018http\u2019)." }, { kind: "fail", ts: "00.901", text: "Request failed after 3 retries." }],
        detail: { label: "ERROR", tone: "error", body: "HTTPError 503 Service Unavailable\nupstream: api.internal/orders\nretries: 3/3 exhausted" } },
      { name: "Transform", id: "transform-c4", type: "transform", state: "skipped", summary: "Skipped \u2014 upstream failed" },
      { name: "Log", id: "log-9f2", type: "log", state: "skipped", summary: "Skipped \u2014 upstream failed" },
      { name: "End", id: "end-kj4v7xq3l", type: "end", state: "skipped" },
    ],
  },
};

Object.assign(window, { TimelinePanel, RUNS });
