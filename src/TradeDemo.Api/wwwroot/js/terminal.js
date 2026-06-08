"use strict";

// ── State ──
const state = {
    prices: new Map(),
    orderFlow: new Map(),
    tickerData: new Map(),
    signalCount: 0,
    msgCount: 0,
    lastMsgCountReset: Date.now(),
    msgPerSec: 0,
    maxFeedRows: 60,
    maxPendingSignals: 2500,
    maxPendingFeedRows: 250,
    renderBatchSize: 140,
    priceRenderIntervalMs: 1500,
    feedRenderIntervalMs: 1200,
    maxFeedRowsPerRender: 1,
    pendingSignals: [],
    pendingPriceUpdates: new Map(),
    pendingFeedRows: [],
    priceRenderTimer: null,
    feedRenderTimer: null,
    lastPriceRenderAt: 0,
    lastFeedRenderAt: 0,
    renderScheduled: false,
    uiRefreshPaused: false,
    tickerDirty: false,
    lastSignalAt: Date.now(),
    reconnectInFlight: false,
    reconnectRetryHandle: null,
    rateHistory: [],
    rateHistoryStartedAt: Date.now(),
    maxRateHistorySeconds: 60,
    lastServerRate: 0,
};

// ── DOM refs ──
const dom = {
    connectionDot: document.getElementById("connectionDot"),
    connectionStatus: document.getElementById("connectionStatus"),
    throughputBadge: document.getElementById("throughputBadge"),
    clock: document.getElementById("clock"),
    pauseResumeButton: document.getElementById("pauseResumeButton"),
    tickerTape: document.getElementById("tickerTape"),
    priceGrid: document.getElementById("priceGrid"),
    signalFeed: document.getElementById("signalFeed"),
    orderFlow: document.getElementById("orderFlow"),
    feedCount: document.getElementById("feedCount"),
    statProcessed: document.getElementById("statProcessed"),
    statDropped: document.getElementById("statDropped"),
    statQueueDepth: document.getElementById("statQueueDepth"),
    statServerRate: document.getElementById("statServerRate"),
    statSnapshotsPerSec: document.getElementById("statSnapshotsPerSec"),
    statCoalescedPerSec: document.getElementById("statCoalescedPerSec"),
    statServerTotal: document.getElementById("statServerTotal"),
    statCpuUsage: document.getElementById("statCpuUsage"),
    statMemoryUsage: document.getElementById("statMemoryUsage"),
    statWorkingSet: document.getElementById("statWorkingSet"),
    statThreadCount: document.getElementById("statThreadCount"),
    rateGraph: document.getElementById("rateGraph"),
    rateTimeAxis: document.getElementById("rateTimeAxis"),
};

// ── Clock ──
function updateClock() {
    const now = new Date();
    dom.clock.textContent = now.toISOString().substring(11, 23);
    requestAnimationFrame(updateClock);
}
updateClock();

// ── Pause/Resume ──
dom.pauseResumeButton.addEventListener("click", () => {
    state.uiRefreshPaused = !state.uiRefreshPaused;
    dom.pauseResumeButton.textContent = state.uiRefreshPaused ? "Resume" : "Pause";

    if (!state.uiRefreshPaused) {
        scheduleRender();
        schedulePriceRender();
        scheduleFeedRender();
    }
});

// ── Event Rate Graph ──
function initRateGraph() {
    const canvas = dom.rateGraph;
    if (!canvas) return;

    const ctx = canvas.getContext("2d");
    const dpr = window.devicePixelRatio || 1;

    // Set canvas size with high DPI support
    const rect = canvas.getBoundingClientRect();
    canvas.width = rect.width * dpr;
    canvas.height = rect.height * dpr;
    ctx.scale(dpr, dpr);

    return { ctx, width: rect.width, height: rect.height };
}

function drawRateGraph() {
    const canvas = dom.rateGraph;
    if (!canvas) return;

    const { ctx, width, height } = initRateGraph();

    // Clear canvas
    ctx.clearRect(0, 0, width, height);

    // Draw background
    ctx.fillStyle = "#0d1c36";
    ctx.fillRect(0, 0, width, height);

    // Draw grid lines
    ctx.strokeStyle = "#1f345c";
    ctx.lineWidth = 1;

    // Horizontal grid lines
    for (let i = 0; i <= 4; i++) {
        const y = (height / 4) * i;
        ctx.beginPath();
        ctx.moveTo(0, y);
        ctx.lineTo(width, y);
        ctx.stroke();
    }

    const history = state.rateHistory;
    const maxRate = Math.max(100, ...history.map((sample) => sample.rate)) * 1.2;

    ctx.strokeStyle = "#1ecb8b";
    ctx.lineWidth = 2;
    ctx.lineJoin = "round";

    if (history.length > 0) {
        ctx.beginPath();
        history.forEach((sample, i) => {
            const x = history.length === 1 ? width : (width / (history.length - 1)) * i;
            const y = height - (sample.rate / maxRate) * height;

            if (i === 0) {
                ctx.moveTo(x, y);
            } else {
                ctx.lineTo(x, y);
            }
        });
        ctx.stroke();

        ctx.lineTo(width, height);
        ctx.lineTo(0, height);
        ctx.closePath();
        ctx.fillStyle = "rgba(30, 203, 139, 0.1)";
        ctx.fill();
    }

    updateRateTimeAxis();

    const currentRate = history.length === 0 ? 0 : history[history.length - 1].rate;
    ctx.fillStyle = "#1ecb8b";
    ctx.font = "bold 12px Consolas, monospace";
    ctx.fillText(`${currentRate.toLocaleString()} msg/s`, 8, 18);
}

function updateRateTimeAxis() {
    const elapsedSeconds = Math.min(state.maxRateHistorySeconds, Math.max(0, Math.floor((Date.now() - state.rateHistoryStartedAt) / 1000)));
    const labels = [elapsedSeconds, Math.round(elapsedSeconds * 0.75), Math.round(elapsedSeconds * 0.5), Math.round(elapsedSeconds * 0.25), 0];
    dom.rateTimeAxis.innerHTML = labels
        .map((secondsAgo) => `<span>${secondsAgo === 0 ? "Now" : `-${secondsAgo}s`}</span>`)
        .join("");
}

// Initialize and draw rate graph
setTimeout(() => drawRateGraph(), 100);
window.addEventListener("resize", drawRateGraph);

// ── Throughput meter ──
setInterval(() => {
    const elapsed = Math.max(0.001, (Date.now() - state.lastMsgCountReset) / 1000);
    state.msgPerSec = Math.round(state.msgCount / elapsed);
    dom.throughputBadge.textContent = `${state.msgPerSec} feeds/s`;
    state.msgCount = 0;
    state.lastMsgCountReset = Date.now();
}, 1000);

// ── SignalR Connection ──
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/tradehub")
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

connection.onreconnecting(() => {
    dom.connectionDot.className = "status-dot";
    dom.connectionStatus.textContent = "RECONNECTING...";
});

connection.onreconnected(() => {
    dom.connectionDot.className = "status-dot connected";
    dom.connectionStatus.textContent = "CONNECTED";
    state.lastSignalAt = Date.now();
});

connection.onclose(() => {
    dom.connectionDot.className = "status-dot disconnected";
    dom.connectionStatus.textContent = "DISCONNECTED";
});

// ── Buffered handlers ──
connection.on("TradeSignal", (signal) => {
    enqueueSignal(signal);
});

connection.on("TradeSignals", (signals) => {
    if (!signals || !Array.isArray(signals)) return;
    for (const signal of signals) {
        enqueueSignal(signal);
    }
});

let lastStatsTime = null;
connection.on("Stats", (stats) => {
    const now = Date.now();

    dom.statProcessed.textContent = Number(stats.processedTotal).toLocaleString();
    dom.statDropped.textContent = Number(stats.droppedTotal).toLocaleString();
    dom.statQueueDepth.textContent = Number(stats.queueDepth).toLocaleString();

    const snapshotsPerSecVal = stats.snapshotsPerSec ?? stats.SnapshotsPerSec;
    if (snapshotsPerSecVal != null) {
        dom.statSnapshotsPerSec.textContent = Math.round(Number(snapshotsPerSecVal)).toLocaleString();
    }

    const coalescedPerSecVal = stats.coalescedPerSec ?? stats.CoalescedPerSec;
    if (coalescedPerSecVal != null) {
        dom.statCoalescedPerSec.textContent = Math.round(Number(coalescedPerSecVal)).toLocaleString();
    }

    // Check both camelCase and PascalCase just in case
    const serverRateVal = stats.serverGenerationRatePerSec ?? stats.ServerGenerationRatePerSec;
    const serverTotalVal = stats.serverGeneratedTotal ?? stats.ServerGeneratedTotal;

    if (serverRateVal != null) {
        const serverRate = Math.round(Number(serverRateVal));
        dom.statServerRate.textContent = serverRate.toLocaleString();
        state.lastServerRate = serverRate;

        // Reset time tracking when we first start receiving stats
        if (lastStatsTime === null) {
            state.rateHistoryStartedAt = now;
            state.rateHistory = [];
        }
        lastStatsTime = now;

        // Add server rate to history for the graph
        const elapsedSeconds = Math.floor((now - state.rateHistoryStartedAt) / 1000);
        state.rateHistory.push({ elapsedSeconds, rate: serverRate });
        while (state.rateHistory.length > state.maxRateHistorySeconds) {
            state.rateHistory.shift();
        }
        drawRateGraph();
    }
    if (serverTotalVal != null) {
        dom.statServerTotal.textContent = Number(serverTotalVal).toLocaleString();
    }
});

function enqueueSignal(signal) {
    state.lastSignalAt = Date.now();
    state.pendingSignals.push(signal);

    if (state.pendingSignals.length > state.maxPendingSignals) {
        // Drop oldest to avoid huge catch-up storms after tab resume.
        state.pendingSignals.splice(0, state.pendingSignals.length - state.maxPendingSignals);
    }

    scheduleRender();
}

function scheduleRender() {
    if (state.uiRefreshPaused || state.renderScheduled) {
        return;
    }

    state.renderScheduled = true;
    requestAnimationFrame(drainSignals);
}

function drainSignals() {
    state.renderScheduled = false;

    if (state.uiRefreshPaused || state.pendingSignals.length === 0) {
        return;
    }

    const latestBySymbol = new Map();

    let processed = 0;
    while (state.pendingSignals.length > 0 && processed < state.renderBatchSize) {
        const signal = state.pendingSignals.shift();
        processed++;

        state.msgCount++;
        state.signalCount++;

        latestBySymbol.set(signal.symbol, signal);
        accumulateOrderFlow(signal);
        enqueueFeedRow(signal);
    }

    latestBySymbol.forEach((signal) => {
        queuePriceUpdate(signal);
        state.tickerData.set(signal.symbol, signal);
    });

    schedulePriceRender();
    scheduleFeedRender();

    renderOrderFlow();

    state.tickerDirty = true;

    // If backlog remains, process on next animation frame without locking main thread.
    if (state.pendingSignals.length > 0) {
        scheduleRender();
    }
}

// ── Price Grid ──
function queuePriceUpdate(signal) {
    state.pendingPriceUpdates.set(signal.symbol, signal);
}

function schedulePriceRender() {
    if (state.uiRefreshPaused || state.priceRenderTimer || state.pendingPriceUpdates.size === 0) {
        return;
    }

    const elapsed = Date.now() - state.lastPriceRenderAt;
    const delay = Math.max(0, state.priceRenderIntervalMs - elapsed);
    state.priceRenderTimer = setTimeout(renderPriceGrid, delay);
}

function renderPriceGrid() {
    state.priceRenderTimer = null;

    if (state.uiRefreshPaused || state.pendingPriceUpdates.size === 0) {
        return;
    }

    const updates = [...state.pendingPriceUpdates.values()];
    state.pendingPriceUpdates.clear();

    for (const signal of updates) {
        updatePriceGrid(signal);
    }

    state.lastPriceRenderAt = Date.now();
}

function updatePriceGrid(signal) {
    let row = document.getElementById(`price-${signal.symbol}`);
    const isUp = signal.change >= 0;

    if (!row) {
        row = document.createElement("div");
        row.className = "price-row";
        row.id = `price-${signal.symbol}`;
        row.innerHTML = `
            <span class="price-symbol"></span>
            <span class="price-bid-ask"></span>
            <span class="price-mid"></span>
            <span class="price-change"></span>
            <span class="price-exchange"></span>
        `;
        dom.priceGrid.appendChild(row);
    }

    row.querySelector(".price-symbol").textContent = signal.symbol;
    row.querySelector(".price-exchange").textContent = signal.exchange;

    const bidAskEl = row.querySelector(".price-bid-ask");
    const spread = signal.askPrice - signal.bidPrice;
    bidAskEl.textContent = `${formatPrice(signal.bidPrice)} / ${formatPrice(signal.askPrice)}`;

    const midEl = row.querySelector(".price-mid");
    midEl.textContent = formatPrice(signal.midPrice);
    midEl.className = `price-mid ${isUp ? "ticker-up" : "ticker-down"}`;

    const changeEl = row.querySelector(".price-change");
    changeEl.textContent = `${isUp ? "+" : ""}${signal.changePercent.toFixed(2)}%`;
    changeEl.className = `price-change ${isUp ? "ticker-up" : "ticker-down"}`;

    // Lightweight flash without forcing synchronous layout.
    row.classList.toggle("flash-green", isUp);
    row.classList.toggle("flash-red", !isUp);

    state.prices.set(signal.symbol, signal);
}

// ── Signal Feed ──
function enqueueFeedRow(signal) {
    state.pendingFeedRows.push(signal);

    if (state.pendingFeedRows.length > state.maxPendingFeedRows) {
        state.pendingFeedRows.splice(0, state.pendingFeedRows.length - state.maxPendingFeedRows);
    }
}

function scheduleFeedRender() {
    if (state.uiRefreshPaused || state.feedRenderTimer || state.pendingFeedRows.length === 0) {
        return;
    }

    const elapsed = Date.now() - state.lastFeedRenderAt;
    const delay = Math.max(0, state.feedRenderIntervalMs - elapsed);
    state.feedRenderTimer = setTimeout(renderFeedRows, delay);
}

function renderFeedRows() {
    state.feedRenderTimer = null;

    if (state.uiRefreshPaused || state.pendingFeedRows.length === 0) {
        return;
    }

    const maxRows = state.maxFeedRowsPerRender || 1;
    const startIdx = Math.max(0, state.pendingFeedRows.length - maxRows);
    const rows = state.pendingFeedRows.slice(startIdx).reverse();
    state.pendingFeedRows.length = 0;

    for (const signal of rows) {
        addSignalRow(signal);
    }

    dom.feedCount.textContent = state.signalCount.toLocaleString();
    state.lastFeedRenderAt = Date.now();
}

function addSignalRow(signal) {
    const row = document.createElement("div");
    const isBuy = signal.direction === "BUY";
    row.className = `signal-row ${isBuy ? "buy" : "sell"}`;

    const time = new Date(signal.timestamp).toISOString().substring(11, 23);
    const execPrice = isBuy ? signal.askPrice : signal.bidPrice;
    row.innerHTML = `
        <span class="signal-time">${time}</span>
        <span class="signal-symbol">${signal.symbol}</span>
        <span class="signal-price ${isBuy ? "ticker-up" : "ticker-down"}">${formatPrice(execPrice)}</span>
        <span class="signal-spread">${formatPrice(signal.bidPrice)}/${formatPrice(signal.askPrice)}</span>
        <span class="signal-vol">${formatVolume(signal.volume)}</span>
        <span class="signal-dir ${isBuy ? "buy" : "sell"}">${signal.direction}</span>
    `;

    dom.signalFeed.prepend(row);

    while (dom.signalFeed.children.length > state.maxFeedRows) {
        dom.signalFeed.lastElementChild.remove();
    }
}

// ── Order Flow ──
function accumulateOrderFlow(signal) {
    if (!state.orderFlow.has(signal.symbol)) {
        state.orderFlow.set(signal.symbol, { buys: 0, sells: 0 });
    }

    const flow = state.orderFlow.get(signal.symbol);
    if (signal.direction === "BUY") {
        flow.buys += signal.volume;
    } else {
        flow.sells += signal.volume;
    }
}

function renderOrderFlow() {
    const sorted = [...state.orderFlow.entries()]
        .map(([sym, f]) => ({ sym, total: f.buys + f.sells, buys: f.buys, sells: f.sells }))
        .sort((a, b) => b.total - a.total)
        .slice(0, 8);

    dom.orderFlow.innerHTML = sorted.map(({ sym, buys, sells, total }) => {
        const buyPct = total > 0 ? Math.round((buys / total) * 100) : 0;
        const sellPct = 100 - buyPct;
        return `
            <div class="flow-bar">
                <span class="flow-symbol">${sym}</span>
                <div class="flow-bar-track">
                    <div class="flow-buy-bar" style="width:${buyPct}%"></div>
                    <div class="flow-sell-bar" style="width:${sellPct}%"></div>
                </div>
                <span class="flow-ratio">${buyPct}%</span>
            </div>
        `;
    }).join("");
}

// ── Ticker Tape ──
setInterval(() => {
    if (!state.tickerDirty) {
        return;
    }

    const items = [...state.tickerData.values()].map((s) => {
        const cls = s.change >= 0 ? "ticker-up" : "ticker-down";
        const arrow = s.change >= 0 ? "▲" : "▼";
        return `<span class="ticker-item">
            <span class="ticker-symbol">${s.symbol}</span>
            <span class="ticker-price ${cls}">${formatPrice(s.midPrice)} ${arrow} ${Math.abs(s.changePercent).toFixed(2)}%</span>
        </span>`;
    });

    dom.tickerTape.innerHTML = items.join("") + items.join("");
    state.tickerDirty = false;
}, 350);

// ── Tab visibility safety ──
document.addEventListener("visibilitychange", async () => {
    if (document.hidden) {
        return;
    }

    // Clear stale backlog accumulated while hidden so we recover instantly.
    state.pendingSignals.length = 0;
    scheduleRender();

    try {
        await ensureHotResume();
    } catch {
        // Recovery retries are handled by reconnect scheduling below.
    }
});

// If feed appears stale while tab is visible, force an immediate reconnect instead
// of waiting for SignalR's exponential/backoff slot (which can be ~10 seconds).
setInterval(() => {
    if (document.hidden) {
        return;
    }

    const ageMs = Date.now() - state.lastSignalAt;
    if (ageMs > 5000 && connection.state !== signalR.HubConnectionState.Connected) {
        forceReconnect("reconnecting...");
    }
}, 2000);

window.addEventListener("beforeunload", async () => {
    try {
        if (connection.state !== signalR.HubConnectionState.Disconnected) {
            await connection.stop();
        }
    } catch {
        // Best-effort cleanup.
    }
});

// ── Utilities ──
function formatPrice(price) {
    return Number(price).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function formatVolume(vol) {
    if (vol >= 1000) return (vol / 1000).toFixed(1) + "K";
    return String(vol);
}

// ── Start ──
async function start(force = false) {
    try {
        if (force) {
            await forceReconnect("start-force");
            return;
        }

        if (connection.state === signalR.HubConnectionState.Disconnected) {
            await connection.start();
        }

        dom.connectionDot.className = "status-dot connected";
        dom.connectionStatus.textContent = "CONNECTED";
        state.lastSignalAt = Date.now();

        if (state.reconnectRetryHandle) {
            clearTimeout(state.reconnectRetryHandle);
            state.reconnectRetryHandle = null;
        }
    } catch (err) {
        dom.connectionDot.className = "status-dot disconnected";
        dom.connectionStatus.textContent = "FAILED";
        console.error("SignalR connection failed:", err);

        if (!state.reconnectRetryHandle) {
            state.reconnectRetryHandle = setTimeout(() => {
                state.reconnectRetryHandle = null;
                start(false);
            }, 1500);
        }
    }
}

async function ensureHotResume() {
    const ageMs = Date.now() - state.lastSignalAt;
    if (connection.state !== signalR.HubConnectionState.Connected || ageMs > 2500) {
        await forceReconnect("reconnecting..");
    }
}

async function forceReconnect(reason) {
    if (state.reconnectInFlight) {
        return;
    }

    state.reconnectInFlight = true;
    try {
        dom.connectionStatus.textContent = `RESUMING (${reason})...`;
        dom.connectionDot.className = "status-dot";

        if (connection.state !== signalR.HubConnectionState.Disconnected) {
            try {
                await connection.stop();
            } catch {
                // Ignore stop race errors.
            }
        }

        await connection.start();
        dom.connectionDot.className = "status-dot connected";
        dom.connectionStatus.textContent = "CONNECTED";
        state.lastSignalAt = Date.now();

        if (state.reconnectRetryHandle) {
            clearTimeout(state.reconnectRetryHandle);
            state.reconnectRetryHandle = null;
        }
    } catch (err) {
        console.warn("Force reconnect failed:", err);
        if (!state.reconnectRetryHandle) {
            state.reconnectRetryHandle = setTimeout(() => {
                state.reconnectRetryHandle = null;
                forceReconnect("retry");
            }, 1200);
        }
    } finally {
        state.reconnectInFlight = false;
    }
}

start();

// ── System Metrics ──
const updateSystemMetrics = async () => {
    try {
        const response = await fetch("/api/system/metrics");
        const metrics = await response.json();

        // Update CPU usage
        const cpuUsage = Math.round(metrics.cpuUsagePercent * 100) / 100;
        dom.statCpuUsage.textContent = `${cpuUsage}%`;
        dom.statCpuUsage.className = `stat-value ${cpuUsage > 80 ? "stat-warn" : cpuUsage > 50 ? "stat-warning" : ""}`;

        // Update memory usage (MB)
        const memoryMb = Math.round(metrics.memoryUsageBytes / 1024 / 1024);
        dom.statMemoryUsage.textContent = `${memoryMb.toLocaleString()} MB`;

        // Update working set (MB)
        const workingSetMb = Math.round(metrics.workingSetBytes / 1024 / 1024);
        dom.statWorkingSet.textContent = `${workingSetMb.toLocaleString()} MB`;

        // Update thread count
        dom.statThreadCount.textContent = metrics.threadCount;
    } catch (err) {
        console.warn("Failed to fetch system metrics:", err);
    }
};

// Update system metrics every second
setInterval(updateSystemMetrics, 1000);
updateSystemMetrics(); // Initial update
