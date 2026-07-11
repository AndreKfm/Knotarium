const MODE_LABEL = {
  foreach: "FOR EACH",
  count: "REPEAT N",
  while: "WHILE",
  batch: "BATCH"
};

function IterTok({ name, dim, size, value }) {
  const cls = `vtok iter ${size === "sm" ? "sm" : size === "lg" ? "lg" : ""} ${dim ? "dim" : ""}`;
  return (
    <span className={cls} style={{ borderColor: "rgba(124, 108, 240, 0.45)", background: "rgba(124, 108, 240, 0.12)", "--tc": "var(--violet)" }}>
      <span className="vtok-glyph" />
      <span className="vtok-fix">$</span>
      <span className="vtok-name">{name.startsWith('$') ? name.slice(1) : name}</span>
      {value && (
        <>
          <span style={{ color: "var(--muted)", margin: "0 4px" }}>:</span>
          <span className="vtok-val">{value}</span>
        </>
      )}
    </span>
  );
}

function IterShelf({ mode }) {
  const tokens = {
    foreach: [ { name: "$item" }, { name: "$index" }, { name: "$isFirst" } ],
    count: [ { name: "$index" }, { name: "$isFirst" }, { name: "$isLast" } ],
    while: [ { name: "$index" }, { name: "$iteration" } ],
    batch: [ { name: "$batch" }, { name: "$index" }, { name: "$isFirst" } ]
  };
  const list = tokens[mode] || [];
  return (
    <div className="kg-shelf" style={{ flex: 1 }}>
      <span className="kg-shelf-label">EXPOSES</span>
      <div className="kg-shelf-toks">
        {list.map((tok, i) => (
          <IterTok key={i} name={tok.name} dim={i > 0} />
        ))}
      </div>
    </div>
  );
}

function MiniNode({ title, category, type, isLit, isDim, left, top, width, hasIn, hasOut }) {
  const cls = `kg-mini ${isLit ? "lit" : ""} ${isDim ? "dim" : ""}`;
  const getIcon = () => {
    if (type === "http") return VIcon.http("var(--teal)");
    if (type === "log") return VIcon.log("var(--violet)");
    if (type === "clock") return VIcon.clock("var(--amber)");
    return VIcon.transform("var(--green)");
  };
  const getIconColor = () => {
    if (type === "http") return "var(--teal)";
    if (type === "log") return "var(--violet)";
    if (type === "clock") return "var(--amber)";
    return "var(--green)";
  };

  return (
    <div className={cls} style={{ left, top, width, "--ac": getIconColor() }}>
      <div className="kg-mini-head">
        <span className="kg-mini-ico">{getIcon()}</span>
        <span className="kg-mini-title" title={title}>{title}</span>
      </div>
      <div className="kg-mini-sub">
        <span>{category}</span>
      </div>
      {hasIn && <span className="kg-mini-port in" />}
      {hasOut && <span className="kg-mini-port out" />}
    </div>
  );
}

function StageFlow({ mode, variant, litIndex }) {
  const nodes = [];
  const edges = [];

  if (mode === "foreach") {
    nodes.push(
      { id: 0, title: "POST /fulfill", category: "HTTP Request", type: "http", left: 60, top: 40, width: 150, hasIn: true, hasOut: true },
      { id: 1, title: "Log Success", category: "Log", type: "log", left: 300, top: 40, width: 130, hasIn: true, hasOut: true }
    );
    edges.push(
      { d: "M 0 70 C 30 70, 30 70, 60 70", lit: variant === "running" && litIndex === 0 },
      { d: "M 210 70 C 255 70, 255 70, 300 70", lit: variant === "running" && litIndex === 1 },
      { d: "M 430 70 C 470 70, 480 125, 245 125 C 50 125, 0 125, 0 70", lit: variant === "running" && litIndex === 2, loopback: true }
    );
  } else if (mode === "count") {
    nodes.push(
      { id: 0, title: "Generate Report", category: "Transform", type: "transform", left: 60, top: 40, width: 150, hasIn: true, hasOut: true },
      { id: 1, title: "Send Email", category: "HTTP Request", type: "http", left: 300, top: 40, width: 130, hasIn: true, hasOut: true }
    );
    edges.push(
      { d: "M 0 70 C 30 70, 30 70, 60 70", lit: false },
      { d: "M 210 70 C 255 70, 255 70, 300 70", lit: false },
      { d: "M 430 70 C 470 70, 480 125, 245 125 C 50 125, 0 125, 0 70", lit: false, loopback: true }
    );
  } else if (mode === "while") {
    nodes.push(
      { id: 0, title: "Poll Status", category: "HTTP Request", type: "http", left: 60, top: 40, width: 150, hasIn: true, hasOut: true },
      { id: 1, title: "Wait 1s", category: "Delay", type: "clock", left: 300, top: 40, width: 130, hasIn: true, hasOut: true }
    );
    edges.push(
      { d: "M 0 70 C 30 70, 30 70, 60 70", lit: false },
      { d: "M 210 70 C 255 70, 255 70, 300 70", lit: false },
      { d: "M 430 70 C 470 70, 480 125, 245 125 C 50 125, 0 125, 0 70", lit: false, loopback: true }
    );
  } else {
    nodes.push(
      { id: 0, title: "Process Chunk", category: "Transform", type: "transform", left: 60, top: 40, width: 150, hasIn: true, hasOut: true },
      { id: 1, title: "Upload Data", category: "HTTP Request", type: "http", left: 300, top: 40, width: 130, hasIn: true, hasOut: true }
    );
    edges.push(
      { d: "M 0 70 C 30 70, 30 70, 60 70", lit: false },
      { d: "M 210 70 C 255 70, 255 70, 300 70", lit: false },
      { d: "M 430 70 C 470 70, 480 125, 245 125 C 50 125, 0 125, 0 70", lit: false, loopback: true }
    );
  }

  return (
    <div className="kg-body-stage" style={{ height: 160, position: "relative" }}>
      <svg className="kg-body-edges" style={{ width: "100%", height: "100%" }}>
        {edges.map((edge, i) => {
          let cls = "kg-edge";
          if (edge.lit) cls += " lit";
          if (edge.loopback) cls += " loopback";
          return <path key={i} d={edge.d} className={cls} />;
        })}
      </svg>
      <span className="kg-entry-port" style={{ left: 0, top: 70 }} />
      {nodes.map((node) => {
        const isNodeLit = variant === "running" && litIndex === node.id;
        const isNodeDim = variant === "running" && litIndex !== node.id;
        return (
          <MiniNode
            key={node.id}
            title={node.title}
            category={node.category}
            type={node.type}
            isLit={isNodeLit}
            isDim={isNodeDim}
            left={node.left}
            top={node.top}
            width={node.width}
            hasIn={node.hasIn}
            hasOut={node.hasOut}
          />
        );
      })}
      <div className="kg-loopback-chip" style={{ top: 125, left: 245 }}>
        {variant === "running" && litIndex === 2 ? "loopback active" : "loopback"}
      </div>
    </div>
  );
}

function LoopNode({ mode, variant, iter, litIndex, width }) {
  const containerWidth = width || 680;
  const isCollapsed = variant === "collapsed";
  const isRunning = variant === "running";

  const getTitle = () => {
    if (mode === "foreach") return "For Each";
    if (mode === "count") return "Repeat N";
    if (mode === "while") return "While Loop";
    return "Batch Loop";
  };

  const getModeLabel = () => MODE_LABEL[mode] || "LOOP";

  return (
    <div className={`kg-loop ${isRunning ? "running" : ""} ${isCollapsed ? "collapsed" : ""}`} style={{ width: containerWidth }}>
      <div className="kg-roletag">LOOP CONTAINER</div>
      <span className="kg-loop-port cin" />
      <span className={`kg-loop-port cout ${isRunning ? "lit" : ""}`} />

      <div className="kg-loop-head">
        <span className="kg-loop-ico">{LIcon.loop("var(--violet)", 18)}</span>
        <span className="kg-loop-title">{getTitle()}</span>
        <span className="kg-mode-pill">{getModeLabel()}</span>
        <span className="kg-head-spacer"></span>
        {isRunning ? (
          <div className="kg-runstate">
            <span className="kg-run-dot" />
            <span className="kg-run-text">Running iteration <b>{iter?.n}</b> of <b>{iter?.total}</b></span>
          </div>
        ) : (
          <span className="kg-idle-tag">IDLE</span>
        )}
        <span className="kg-chev">▲</span>
      </div>

      {isCollapsed ? (
        <div style={{ display: "flex", alignItems: "center", marginTop: 10 }}>
          <div className="kg-collapse-summary">18 iterations · 2 nodes inside</div>
          <div className="kg-produces">
            Produces <IterTok name="$loop.results" size="sm" /> <span className="kg-foot-type">ARRAY</span>
          </div>
        </div>
      ) : (
        <>
          <div className="kg-loop-config">
            {mode === "foreach" && (
              <div className="kg-cfg">
                <span className="kg-cfg-word">For each</span>
                <div className="kg-cfgchip" style={{ borderColor: "rgba(124, 108, 240, 0.4)", background: "rgba(124, 108, 240, 0.08)" }}>
                  <span className="kg-cfgdiamond" style={{ background: "var(--violet)" }} />
                  <span style={{ color: "#c3b9ff" }}>item</span>
                </div>
                <span className="kg-cfg-word">in</span>
                <div className="kg-cfgchip" style={{ borderColor: "rgba(34, 211, 238, 0.4)", background: "rgba(34, 211, 238, 0.08)" }}>
                  <span className="kg-cfgdiamond" style={{ background: "var(--teal)" }} />
                  <span style={{ color: "var(--teal)" }}>$get_orders.body</span>
                </div>
                <span className="kg-cfg-count">(18 items)</span>
              </div>
            )}
            {mode === "count" && (
              <div className="kg-cfg">
                <span className="kg-cfg-word">Repeat</span>
                <div className="kg-cfgchip num" style={{ borderColor: "rgba(127, 231, 216, 0.4)", background: "rgba(127, 231, 216, 0.08)" }}>
                  10
                </div>
                <span className="kg-cfg-word">times</span>
              </div>
            )}
            {mode === "while" && (
              <div className="kg-cfg">
                <span className="kg-cfg-word">While</span>
                <div className="kg-cfgchip" style={{ borderColor: "rgba(124, 108, 240, 0.4)", background: "rgba(124, 108, 240, 0.08)" }}>
                  <span className="kg-cfgdiamond" style={{ background: "var(--violet)" }} />
                  <span style={{ color: "#c3b9ff" }}>$index</span>
                </div>
                <span className="kg-cfg-op">&lt;</span>
                <div className="kg-cfgchip num" style={{ borderColor: "rgba(127, 231, 216, 0.4)", background: "rgba(127, 231, 216, 0.08)" }}>
                  100
                </div>
              </div>
            )}
            {mode === "batch" && (
              <div className="kg-cfg">
                <span className="kg-cfg-word">Batch</span>
                <div className="kg-cfgchip" style={{ borderColor: "rgba(34, 211, 238, 0.4)", background: "rgba(34, 211, 238, 0.08)" }}>
                  <span className="kg-cfgdiamond" style={{ background: "var(--teal)" }} />
                  <span style={{ color: "var(--teal)" }}>$get_orders.body</span>
                </div>
                <span className="kg-cfg-word">by size</span>
                <div className="kg-cfgchip num" style={{ borderColor: "rgba(127, 231, 216, 0.4)", background: "rgba(127, 231, 216, 0.08)" }}>
                  5
                </div>
              </div>
            )}
          </div>

          {isRunning && iter && (
            <div className="kg-progress">
              <div className="kg-progress-bar">
                <span style={{ width: `${(iter.n / iter.total) * 100}%` }} />
              </div>
            </div>
          )}

          <div className="kg-loop-body">
            <div className="kg-body-label">
              LOOP BODY <span className="kg-body-sub">SUB-FLOW</span>
            </div>
            <IterShelf mode={mode} />
            <StageFlow mode={mode} variant={variant} litIndex={litIndex} />
          </div>

          <div className="kg-loop-foot">
            <div className="kg-foot-item">
              Produces <IterTok name="$loop.results" size="sm" /> <span className="kg-foot-type">ARRAY</span>
            </div>
            <div className="kg-foot-item break">
              <span className="kg-break-dot" />
              Break on failure
            </div>
          </div>
        </>
      )}
    </div>
  );
}

Object.assign(window, { IterTok, IterShelf, LoopNode, MODE_LABEL });
