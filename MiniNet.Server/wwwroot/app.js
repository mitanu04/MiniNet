'use strict';

// ── vis.js topology ───────────────────────────────────────────────────────────
const nodes = new vis.DataSet();
const edges = new vis.DataSet();

let network = null;

function initGraph() {
  const container = document.getElementById('topology-graph');
  if (!container) { console.error('topology-graph container not found'); return; }
  try {
    network = new vis.Network(
      container,
      { nodes, edges },
      {
        physics: { stabilization: { iterations: 100 }, barnesHut: { gravitationalConstant: -3000 } },
        edges: {
          color: { color: '#30363d', highlight: '#388bfd' },
          font: { color: '#8b949e', size: 10, align: 'middle' },
          smooth: { type: 'continuous' }
        },
        nodes: {
          font: { color: '#e6edf3', size: 12, face: 'monospace' },
          borderWidth: 2,
          shadow: { enabled: true, size: 6, color: 'rgba(0,0,0,.4)' }
        },
        interaction: { hover: true, tooltipDelay: 200 }
      }
    );
    network.on('oncontext', params => {
      params.event.preventDefault();
      const nodeId = network.getNodeAt(params.pointer.DOM);
      if (!nodeId) return;
      const n = nodes.get(nodeId);
      if (!n) return;
      if (n.type === 'switch') deleteSwitch(nodeId);
      else if (n.type === 'simhost') deleteDevice(nodeId);
      else if (n.type === 'router') deleteRouter(nodeId);
    });
  } catch (e) {
    console.error('vis.Network init failed:', e);
    container.style.cssText = 'display:flex;align-items:center;justify-content:center;color:#f85149;font-family:monospace';
    container.textContent = 'Graph init error: ' + e.message;
  }
}

const NODE_STYLES = {
  switch:  { shape: 'box',     color: { background: '#1a3a5c', border: '#388bfd', highlight: { background: '#1e4a7a', border: '#58a6ff' } } },
  router:  { shape: 'diamond', color: { background: '#3d2200', border: '#f0883e', highlight: { background: '#5a3300', border: '#ffa657' } } },
  host:    { shape: 'dot',     color: { background: '#0f3320', border: '#3fb950', highlight: { background: '#1a4d30', border: '#56d364' } }, size: 16 },
  simhost: { shape: 'dot',     color: { background: '#1a1a3a', border: '#bc8cff', highlight: { background: '#2a2a5a', border: '#d2b0ff' } }, size: 16 }
};

function renderTopology(topology) {
  const newNodeIds = new Set(topology.nodes.map(n => n.id));
  const newEdgeIds = new Set(topology.edges.map((_, i) => `e${i}`));

  // Remove stale nodes/edges
  nodes.getIds().filter(id => !newNodeIds.has(id)).forEach(id => nodes.remove(id));

  topology.nodes.forEach(n => {
    const style = NODE_STYLES[n.type] || {};
    const node = { id: n.id, label: n.label, title: n.tooltip || n.label, type: n.type, ...style };
    if (nodes.get(n.id)) nodes.update(node);
    else nodes.add(node);
  });

  edges.clear();
  topology.edges.forEach((e, i) => {
    edges.add({ id: `e${i}`, from: e.from, to: e.to, label: e.label || '' });
  });
}

// ── event log ─────────────────────────────────────────────────────────────────
const MAX_EVENTS = 200;

const EVENT_ICONS = {
  SwitchCreated:      '⊞ ',
  DeviceConnected:    '⬤ ',
  DeviceDisconnected: '⊘ ',
  GratuitousArp:      '📢 ',
  ArpRequest:         '? ',
  ArpReply:           '✓ ',
  IcmpEchoRequest:    '→ ',
  IcmpEchoReply:      '← ',
  FrameForwarded:     '↗ ',
  FrameFlooded:       '⊕ ',
  PacketDropped:      '✗ ',
  TtlExpired:         '⏱ ',
  TcpSyn:             '🤝 ',
  TcpSynAck:          '🤝 ',
  TcpAck:             '✔ ',
  TcpData:            '📦 ',
  TcpRetransmit:      '🔁 ',
  TcpFin:             '⛔ '
};

function addEvent(event) {
  const log = document.getElementById('event-log');
  const entry = document.createElement('div');
  const type = event.eventType || 'unknown';
  entry.className = `event-entry ${type.toLowerCase()}`;
  const icon = EVENT_ICONS[type] || '· ';
  entry.innerHTML = `<span class="time">${event.timestamp}</span>${icon}${escapeHtml(event.description)}`;
  entry.title = JSON.stringify(event, null, 2);
  log.insertBefore(entry, log.firstChild);
  while (log.children.length > MAX_EVENTS) log.removeChild(log.lastChild);
}

function clearLog() {
  document.getElementById('event-log').innerHTML = '';
}

function escapeHtml(str) {
  return (str || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

// ── tables panel ──────────────────────────────────────────────────────────────
// Tracks mac tables and arp info gathered from events
const state = { macTables: {}, arpTables: {}, devices: [], switches: [], routers: [] };

function updateTablesPanel() {
  const container = document.getElementById('tables-content');
  let html = '';

  // Routers table
  if (state.routers.length > 0) {
    const rRows = state.routers.map(r => `
      <tr>
        <td>${escapeHtml(r)}</td>
        <td><button class="del-btn" onclick="deleteRouter('${escapeHtml(r)}')">✕</button></td>
      </tr>`).join('');
    html += `<div class="table-block"><h3>Routers (${state.routers.length})</h3><table>
      <tr><th>Name</th><th></th></tr>${rRows}</table></div>`;
  }

  // Switches table
  if (state.switches.length > 0) {
    const swRows = state.switches.map(sw => `
      <tr>
        <td>${escapeHtml(sw)}</td>
        <td><button class="del-btn" onclick="deleteSwitch('${escapeHtml(sw)}')">✕</button></td>
      </tr>`).join('');
    html += `<div class="table-block"><h3>Switches (${state.switches.length})</h3><table>
      <tr><th>Name</th><th></th></tr>${swRows}</table></div>`;
  }

  // Devices table
  if (state.devices.length > 0) {
    const devRows = state.devices.map(d => `
      <tr>
        <td>${escapeHtml(d.name)}</td>
        <td>${escapeHtml(d.ip)}</td>
        <td style="font-family:monospace">${escapeHtml(d.mac)}</td>
        <td>${escapeHtml(d.switch)}</td>
        <td style="color:${d.type === 'simhost' ? 'var(--purple)' : 'var(--green)'}">${d.type === 'simhost' ? 'sim' : 'real'}</td>
        <td>${d.type === 'simhost'
          ? `<button class="del-btn" onclick="deleteDevice('${escapeHtml(d.name)}')">✕</button>`
          : ''}</td>
      </tr>`).join('');
    html += `<div class="table-block"><h3>Devices (${state.devices.length})</h3><table>
      <tr><th>Name</th><th>IP</th><th>MAC</th><th>Switch</th><th>Type</th><th></th></tr>
      ${devRows}</table></div>`;
  }

  container.innerHTML = html || '<p class="empty-tables">No devices connected.</p>';
}

function deleteDevice(name) {
  if (!confirm(`Delete device "${name}"?`)) return;
  connection.invoke('DeleteDevice', name).catch(err => alert('Error: ' + err));
}

function deleteRouter(name) {
  if (!confirm(`Delete router "${name}"? Connected switches will lose their gateway.`)) return;
  connection.invoke('DeleteRouter', name).catch(err => alert('Error: ' + err));
}

function deleteSwitch(name) {
  if (!confirm(`Delete switch "${name}" and all its devices?`)) return;
  connection.invoke('DeleteSwitch', name).catch(err => alert('Error: ' + err));
}

// ── Controls ──────────────────────────────────────────────────────────────────

function addRouter() {
  const name = document.getElementById('inp-router').value.trim();
  if (!name) { alert('Enter a router name.'); return; }
  connection.invoke('CreateRouter', name)
    .then(() => { document.getElementById('inp-router').value = ''; })
    .catch(err => alert('Error: ' + err));
}

function addSwitch() {
  const name   = document.getElementById('inp-sw').value.trim();
  const router = document.getElementById('inp-sw-router').value || null;
  if (!name) { alert('Enter a switch name.'); return; }
  connection.invoke('CreateSwitch', name, router)
    .then(() => { document.getElementById('inp-sw').value = ''; })
    .catch(err => alert('Error: ' + err));
}

function updateRouterDropdown(topology) {
  const sel = document.getElementById('inp-sw-router');
  const current = sel.value;
  sel.innerHTML = '<option value="">— Router (optional) —</option>';
  topology.nodes
    .filter(n => n.type === 'router')
    .forEach(n => {
      const opt = document.createElement('option');
      opt.value = n.id;
      opt.textContent = n.id;
      if (n.id === current) opt.selected = true;
      sel.appendChild(opt);
    });
}

function addDevice() {
  const name = document.getElementById('inp-dev-name').value.trim();
  const sw   = document.getElementById('inp-dev-sw').value;

  if (!name || !sw) {
    alert('Enter a device name and select a switch.');
    return;
  }

  connection.invoke('CreateDevice', { name, switch: sw })
    .then(() => { document.getElementById('inp-dev-name').value = ''; })
    .catch(err => alert('Error: ' + err));
}

function updateSwitchDropdown(topology) {
  const sel = document.getElementById('inp-dev-sw');
  const current = sel.value;
  sel.innerHTML = '<option value="">— Switch —</option>';
  topology.nodes
    .filter(n => n.type === 'switch')
    .forEach(n => {
      const opt = document.createElement('option');
      opt.value = n.id;
      opt.textContent = n.id;
      if (n.id === current) opt.selected = true;
      sel.appendChild(opt);
    });
}

// Parse device info from topology updates
function syncStateFromTopology(topology) {
  state.routers  = topology.nodes.filter(n => n.type === 'router').map(n => n.id);
  state.switches = topology.nodes.filter(n => n.type === 'switch').map(n => n.id);

  state.devices = topology.nodes
    .filter(n => n.type === 'host' || n.type === 'simhost')
    .map(n => {
      const label = n.label || '';
      const lines = label.split('\n');
      return { name: lines[0], ip: lines[1] || '', mac: lines[2] || '', type: n.type, switch: '' };
    });

  topology.edges.forEach(e => {
    const device = state.devices.find(d => d.name === e.from);
    if (device) device.switch = e.to;
  });

  updateTablesPanel();
}

// ── SignalR connection ────────────────────────────────────────────────────────
const connection = new signalR.HubConnectionBuilder()
  .withUrl('/networkHub')
  .withAutomaticReconnect()
  .build();

connection.on('TopologyUpdate', topology => {
  renderTopology(topology);
  syncStateFromTopology(topology);
  updateSwitchDropdown(topology);
  updateRouterDropdown(topology);
});

connection.on('NetworkEvent', event => {
  addEvent(event);
});

connection.onreconnecting(() => setStatus(false));
connection.onreconnected(() => {
  setStatus(true);
  connection.invoke('JoinDashboard');
});

function setStatus(connected) {
  const el = document.getElementById('connection-status');
  el.textContent = connected ? 'Connected' : 'Reconnecting…';
  el.className = `status ${connected ? 'connected' : 'disconnected'}`;
}

async function start() {
  try {
    await connection.start();
    setStatus(true);
    await connection.invoke('JoinDashboard');
  } catch (err) {
    setStatus(false);
    console.error('SignalR connection failed:', err);
    setTimeout(start, 3000);
  }
}

// ── Footer tabs ───────────────────────────────────────────────────────────────
function showTab(name) {
  document.getElementById('tab-devices').style.display = name === 'devices' ? '' : 'none';
  document.getElementById('tab-history').style.display = name === 'history' ? '' : 'none';
  document.querySelectorAll('.footer-tab').forEach(b => b.classList.remove('active'));
  event.target.classList.add('active');
  if (name === 'history') loadHistory();
}

// ── History panel ─────────────────────────────────────────────────────────────
async function loadHistory() {
  const container = document.getElementById('history-content');
  container.innerHTML = '<p class="empty-tables">Loading…</p>';
  try {
    const res = await fetch('/api/events');
    if (!res.ok) { container.innerHTML = `<p class="empty-tables" style="color:var(--red)">Server error ${res.status}</p>`; return; }
    const events = await res.json();
    if (events.length === 0) {
      container.innerHTML = '<p class="empty-tables">No events yet — add a switch or device first.</p>';
      return;
    }
    const rows = events.map(e => `
      <tr>
        <td style="color:var(--text-muted);font-variant-numeric:tabular-nums;white-space:nowrap">${escapeHtml(e.timestamp)}</td>
        <td><span class="ev-badge ${(e.eventType||'').toLowerCase()}">${escapeHtml(e.eventType)}</span></td>
        <td>${escapeHtml(e.description)}</td>
      </tr>`).join('');
    container.innerHTML = `<table style="width:100%">
      <tr><th>Time</th><th>Type</th><th>Description</th></tr>${rows}</table>`;
  } catch (e) {
    container.innerHTML = `<p class="empty-tables" style="color:var(--red)">Error: ${escapeHtml(e.message)}</p>`;
  }
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', () => { initGraph(); start(); });
} else {
  initGraph();
  start();
}
