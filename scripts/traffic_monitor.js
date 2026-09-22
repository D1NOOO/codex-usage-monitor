#!/usr/bin/env node
/*
 * traffic_monitor.js - per-host traffic meter for Clash Verge / mihomo
 *
 * Reads the mihomo REST controller (named pipe \\.\pipe\verge-mihomo by
 * default, or TCP via --controller host:port) and aggregates the cumulative
 * per-connection upload/download counters into per-host deltas. This makes
 * it easy to quantify how much traffic a background tool (e.g. Codex
 * desktop / CodexRateMonitor's app-server restarts) actually generates.
 *
 * Zero dependencies (node >= 16). Run:
 *   node traffic_monitor.js [--controller 127.0.0.1:9097] [--secret XXX]
 *                           [--interval 5] [--filter chatgpt,openai]
 *                           [--csv path.csv] [--summary 60]
 *
 * Ctrl+C prints a final summary. CSV lines: ts,host,down_bytes,up_bytes
 */
"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

// ---- arguments ----
const args = process.argv.slice(2);
function argOf(name, fallback) {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
}
const PIPE = "\\\\.\\pipe\\verge-mihomo";
const useTcp = args.indexOf("--controller") >= 0;
const controller = argOf("--controller", "127.0.0.1:9097");
const secret = argOf("--secret", "set-your-secret");
const intervalMs = Math.max(1000, parseInt(argOf("--interval", "5"), 10) * 1000);
const summaryEvery = Math.max(10, parseInt(argOf("--summary", "60"), 10));
const durationSec = parseInt(argOf("--duration", "0"), 10); // 0 = until Ctrl+C
const csvPath = argOf("--csv", null);
const filterArg = argOf("--filter", null);
const filters = filterArg
  ? filterArg.split(",").map((s) => new RegExp(s.trim(), "i"))
  : null;

// ---- state ----
// last cumulative counters per connection id
const lastByConn = new Map(); // id -> {down, up, host}
// aggregated deltas
const totals = new Map(); // host -> {down, up, conns: Set}
let startedAt = Date.now();
let lastSummaryAt = startedAt;
let lastPrintAt = startedAt;

function request(path) {
  return new Promise((resolve) => {
    const opts = {
      path,
      method: "GET",
      headers: { Host: "mihomo", Authorization: "Bearer " + secret },
      timeout: 5000,
    };
    if (useTcp) {
      const parts = controller.split(":");
      opts.host = parts[0];
      opts.port = parseInt(parts[1] || "9097", 10);
    } else {
      opts.socketPath = PIPE;
    }
    const req = http.request(opts, (res) => {
      let body = "";
      res.on("data", (c) => (body += c));
      res.on("end", () => resolve({ code: res.statusCode, body }));
    });
    req.on("error", (e) => resolve({ code: 0, body: String(e) }));
    req.on("timeout", () => { req.destroy(); resolve({ code: 0, body: "timeout" }); });
    req.end();
  });
}

function hostEntry(host) {
  let e = totals.get(host);
  if (!e) {
    e = { down: 0, up: 0, conns: 0 };
    totals.set(host, e);
  }
  return e;
}

function fmtBytes(n) {
  if (n >= 1048576) return (n / 1048576).toFixed(2) + " MB";
  if (n >= 1024) return (n / 1024).toFixed(1) + " KB";
  return n + " B";
}

function matched(host) {
  if (!filters) return true;
  return filters.some((re) => re.test(host));
}

async function sample() {
  const { code, body } = await request("/connections");
  if (code !== 200) {
    console.error(new Date().toISOString().slice(11, 19),
      "controller error HTTP", code, body.slice(0, 120));
    return;
  }
  let data;
  try { data = JSON.parse(body); } catch { return; }
  const conns = data.connections || [];
  const seen = new Set();
  let deltaDown = 0;
  let deltaUp = 0;
  for (const c of conns) {
    const id = c.id;
    const m = c.metadata || {};
    const host = m.host || m.sniffHost || m.destinationIP || "(unknown)";
    const down = c.download || 0;
    const up = c.upload || 0;
    seen.add(id);
    const prev = lastByConn.get(id);
    if (prev) {
      const dDown = Math.max(0, down - prev.down);
      const dUp = Math.max(0, up - prev.up);
      if (dDown || dUp) {
        const e = hostEntry(host);
        e.down += dDown;
        e.up += dUp;
        deltaDown += dDown;
        deltaUp += dUp;
      }
    } else {
      hostEntry(host).conns += 1;
    }
    lastByConn.set(id, { down, up, host });
  }
  // drop closed connections from tracking
  for (const id of lastByConn.keys()) {
    if (!seen.has(id)) lastByConn.delete(id);
  }

  const now = Date.now();
  if (now - lastSummaryAt >= summaryEvery * 1000) {
    const mins = (now - startedAt) / 60000;
    console.log("\n=== summary after " + mins.toFixed(1) + " min ===");
    const rows = [...totals.entries()]
      .filter(([h]) => matched(h))
      .sort((a, b) => b[1].down - a[1].down)
      .slice(0, 20);
    console.log("  down        up       conns  host");
    let tDown = 0;
    let tUp = 0;
    for (const [host, e] of rows) {
      tDown += e.down;
      tUp += e.up;
      console.log(
        fmtBytes(e.down).padStart(10),
        fmtBytes(e.up).padStart(10),
        String(e.conns).padStart(6),
        " ", host);
    }
    const minutes = (now - lastSummaryAt) / 60000;
    console.log(
      "  interval: " + fmtBytes(deltaDown) + " down / " +
      fmtBytes(deltaUp) + " up in " + minutes.toFixed(1) + " min" +
      "  (avg " + fmtBytes(deltaDown / Math.max(0.1, minutes)) + "/min down)");
    lastSummaryAt = now;
  }
}

function finalSummary() {
  console.log("\n=== final summary ===");
  let tDown = 0;
  let tUp = 0;
  for (const [host, e] of totals) {
    if (!matched(host)) continue;
    tDown += e.down;
    tUp += e.up;
    console.log(fmtBytes(e.down).padStart(10), fmtBytes(e.up).padStart(10),
      "  ", host);
  }
  console.log("TOTAL", fmtBytes(tDown), "down /", fmtBytes(tUp), "up");
}

function main() {
  console.log("traffic monitor: " + (useTcp ? "tcp://" + controller : "pipe " + PIPE));
  console.log("interval " + intervalMs / 1000 + "s, filter " +
    (filters ? filterArg : "(all hosts)"), ", csv " + (csvPath || "(off)"));
  console.log("Ctrl+C to stop and print final summary.\n");
  const loop = async () => {
    try { await sample(); } catch (e) { console.error("sample error", e); }
  };
  loop();
  setInterval(loop, intervalMs);
  setInterval(() => {
    // periodic CSV flush of current totals
    if (!csvPath) return;
    const ts = new Date().toISOString();
    const lines = [];
    for (const [host, e] of totals) {
      if (!matched(host)) continue;
      lines.push([ts, host, e.down, e.up].join(","));
    }
    if (lines.length) {
      fs.appendFileSync(csvPath, lines.join("\n") + "\n");
    }
  }, 30000);
  if (durationSec > 0) {
    setTimeout(finalSummary, durationSec * 1000 + 500);
    setTimeout(() => process.exit(0), durationSec * 1000 + 1500);
  }
  process.on("SIGINT", () => {
    finalSummary();
    process.exit(0);
  });
}

main();
