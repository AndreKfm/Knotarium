function PairedScene() {
  return (
    <div className="kg-scene" style={{ display: "flex", flexDirection: "column", height: "100%", width: "100%" }}>
      <div className="kg-modelcard">
        <div className="kg-illus" style={{ height: 260 }}>
          <svg style={{ position: "absolute", inset: 0, width: "100%", height: "100%" }}>
            <defs>
              <marker id="arrow" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
                <path d="M 0 2 L 10 5 L 0 8 z" fill="#2c3a4d" />
              </marker>
              <marker id="arrow-lit" viewBox="0 0 10 10" refX="6" refY="5" markerWidth="6" markerHeight="6" orient="auto-start-reverse">
                <path d="M 0 2 L 10 5 L 0 8 z" fill="var(--violet)" />
              </marker>
            </defs>
            <path d="M 120 130 H 170" fill="none" stroke="#2c3a4d" strokeWidth="2" />
            <path d="M 285 130 H 330" fill="none" stroke="#2c3a4d" strokeWidth="2" />
            <path d="M 430 130 H 455" fill="none" stroke="#2c3a4d" strokeWidth="2" />
            <path d="M 380 130 C 380 200, 227 200, 227 130" fill="none" stroke="var(--violet)" strokeWidth="2" strokeDasharray="4 3" markerEnd="url(#arrow-lit)" />
          </svg>

          <div className="kg-cnode" style={{ left: 10, top: 105, borderColor: "var(--violet)" }}>
            <span className="kg-cnode-ico">{LIcon.loop("var(--violet)", 14)}</span>
            <span className="kg-cnode-title">Loop Start</span>
          </div>

          <div className="kg-cnode" style={{ left: 170, top: 105, borderColor: "#212b39" }}>
            <span className="kg-cnode-ico">{VIcon.http("#22d3ee")}</span>
            <span className="kg-cnode-title">POST /fulfill</span>
          </div>

          <div className="kg-cnode" style={{ left: 330, top: 105, borderColor: "#212b39" }}>
            <span className="kg-cnode-ico">{VIcon.log("#7c6cf0")}</span>
            <span className="kg-cnode-title">Log success</span>
          </div>

          <div className="kg-cnode" style={{ left: 455, top: 105, borderColor: "var(--violet)" }}>
            <span className="kg-cnode-ico">{LIcon.loop("var(--violet)", 14)}</span>
            <span className="kg-cnode-title">Loop End</span>
          </div>
        </div>

        <div className="kg-tradeoffs">
          <div style={{ fontSize: 13, fontWeight: 700, color: "var(--muted)", marginBottom: 8 }}>TRADEOFFS</div>
          <div className="kg-to-row bad"><span style={{ color: "var(--red)", marginRight: 6 }}>✕</span> Canvas clutter: two nodes for one loop concept</div>
          <div className="kg-to-row bad"><span style={{ color: "var(--red)", marginRight: 6 }}>✕</span> Wiring mess: requires loopback wires crossing other nodes</div>
          <div className="kg-to-row good"><span style={{ color: "var(--green)", marginRight: 6 }}>✓</span> Simple implementation in traditional graph execution</div>
        </div>
      </div>
    </div>
  );
}

function ContainerScene() {
  return (
    <div className="kg-scene" style={{ display: "flex", flexDirection: "column", height: "100%", width: "100%" }}>
      <span className="kg-reco">RECOMMENDED</span>
      <div className="kg-modelcard">
        <div className="kg-illus" style={{ height: 260 }}>
          <div style={{ transform: "scale(0.72)", transformOrigin: "center" }}>
            <LoopNode mode="foreach" variant="idle" width={560} />
          </div>
        </div>

        <div className="kg-tradeoffs">
          <div style={{ fontSize: 13, fontWeight: 700, color: "var(--muted)", marginBottom: 8 }}>TRADEOFFS</div>
          <div className="kg-to-row good"><span style={{ color: "var(--green)", marginRight: 6 }}>✓</span> Visual enclosure: clearly groups nodes inside the loop</div>
          <div className="kg-to-row good"><span style={{ color: "var(--green)", marginRight: 6 }}>✓</span> Scoped variables: shelf makes variables drag-and-dropable</div>
          <div className="kg-to-row good"><span style={{ color: "var(--green)", marginRight: 6 }}>✓</span> Cleaner canvas: loop logic is self-contained</div>
          <div className="kg-to-row bad"><span style={{ color: "var(--red)", marginRight: 6 }}>✕</span> Complex layout engine requirements for nesting</div>
        </div>
      </div>
    </div>
  );
}

function DrillInScene() {
  return (
    <div className="kg-scene" style={{ display: "flex", flexDirection: "column", height: "100%", width: "100%" }}>
      <div className="kg-modelcard">
        <div className="kg-illus" style={{ height: 260 }}>
          <div className="kg-drillnode">
            <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
              <span className="kg-loop-ico">{LIcon.loop("var(--violet)", 18)}</span>
              <span style={{ fontSize: 15, fontWeight: 700 }}>For Each Loop</span>
              <span className="kg-mode-pill" style={{ marginLeft: "auto" }}>FOREACH</span>
            </div>
            <div className="kg-drill-row">
              <span className="kg-drill-meta">2 nodes inside</span>
              <button className="kg-drill-enter">
                Drill In <svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" strokeWidth="2" style={{ marginLeft: 4, verticalAlign: "middle" }}><path d="M1 5h8M6 2l3 3-3 3" /></svg>
              </button>
            </div>
          </div>
          <div className="kg-ghostcanvas">
            <div style={{ position: "absolute", top: 8, left: 12, fontSize: 9, fontWeight: 800, color: "#44505f" }}>SUB-CANVAS PREVIEW</div>
            <div style={{ display: "flex", gap: 8, alignItems: "center", justifyContent: "center", height: "100%" }}>
              <div className="kg-cnode ghost" style={{ transform: "scale(0.7)", position: "relative" }}>
                <span className="kg-cnode-title">POST /fulfill</span>
              </div>
              <div style={{ color: "#1d2634", fontSize: 12 }}>➔</div>
              <div className="kg-cnode ghost" style={{ transform: "scale(0.7)", position: "relative" }}>
                <span className="kg-cnode-title">Log success</span>
              </div>
            </div>
          </div>
        </div>

        <div className="kg-tradeoffs">
          <div style={{ fontSize: 13, fontWeight: 700, color: "var(--muted)", marginBottom: 8 }}>TRADEOFFS</div>
          <div className="kg-to-row good"><span style={{ color: "var(--green)", marginRight: 6 }}>✓</span> Best scalability: handle loops with 50+ nodes easily</div>
          <div className="kg-to-row bad"><span style={{ color: "var(--red)", marginRight: 6 }}>✕</span> Out of sight: hard to see loop behavior at a glance</div>
          <div className="kg-to-row bad"><span style={{ color: "var(--red)", marginRight: 6 }}>✕</span> Navigation friction: requires clicking in and out</div>
        </div>
      </div>
    </div>
  );
}

function Anatomy() {
  const annotations = [
    { num: 1, text: "Header displays loop mode (e.g. For-Each) and execution status (e.g. Running iteration 7 of 18)." },
    { num: 2, text: "Config row defines input items and parameters of iteration." },
    { num: 3, text: "Token shelf exposes scoped iteration variables ($item, $index, $isFirst) for drag-and-drop." },
    { num: 4, text: "Sub-flow canvas holds nested nodes executed on each iteration." },
    { num: 5, text: "Loopback connection visualizes the cycle back to start." },
    { num: 6, text: "Footer lists loop output variables (e.g. array of results) and interruption triggers." }
  ];

  return (
    <div style={{ display: "flex", gap: 30, alignItems: "center", width: "100%", height: "100%", padding: "10px 20px" }}>
      <div style={{ flex: 1 }}>
        <LoopNode
          mode="foreach"
          variant="running"
          iter={{ n: 7, total: 18, item: "#1043" }}
          litIndex={1}
          width={680}
        />
      </div>
      <div className="kg-anno-list" style={{ flexShrink: 0 }}>
        <div style={{ fontSize: 14, fontWeight: 800, color: "var(--violet)", letterSpacing: "0.05em", marginBottom: 8 }}>ANATOMY KEY</div>
        {annotations.map((anno) => (
          <div className="kg-anno" key={anno.num}>
            <div className="kg-anno-num">{anno.num}</div>
            <div>{anno.text}</div>
          </div>
        ))}
      </div>
    </div>
  );
}

Object.assign(window, { PairedScene, ContainerScene, DrillInScene, Anatomy });
