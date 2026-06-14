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
    lastConsumedFills: [],
    consumedFillIds: new Set(),
    maxConsumedFills: 50,
    maxLifecycleRows: 80,
    lifecycleEventIds: new Set(),
    latestMarketMakerState: null,
    currentPosition: 0,
    maxPosition: 1000,
    manualOrderQuantity: 100,
    orderSubmitInFlight: false,
    demoResetInFlight: false,
    latestLatency: null,
    latestSlippage: null,
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
    buyEsButton: document.getElementById("buyEsButton"),
    resetDemoButton: document.getElementById("resetDemoButton"),
    marketMakerToggle: document.getElementById("marketMakerToggle"),
    depthBook: document.getElementById("depthBook"),
    consumedLiquidity: document.getElementById("consumedLiquidity"),
    avgFillBadge: document.getElementById("avgFillBadge"),
    positionQty: document.getElementById("positionQty"),
    positionAvg: document.getElementById("positionAvg"),
    positionRealized: document.getElementById("positionRealized"),
    positionUnrealized: document.getElementById("positionUnrealized"),
    orderLifecycle: document.getElementById("orderLifecycle"),
    openOrders: document.getElementById("openOrders"),
    execOrdersSent: document.getElementById("execOrdersSent"),
    execOrdersFilled: document.getElementById("execOrdersFilled"),
    execCancels: document.getElementById("execCancels"),
    execFillRatio: document.getElementById("execFillRatio"),
    execPnl: document.getElementById("execPnl"),
    slipArrival: document.getElementById("slipArrival"),
    slipValue: document.getElementById("slipValue"),
    latTotal: document.getElementById("latTotal"),
    latRisk: document.getElementById("latRisk"),
    latRoute: document.getElementById("latRoute"),
    latExchange: document.getElementById("latExchange"),
    latFill: document.getElementById("latFill"),
    latOther: document.getElementById("latOther"),
    mmInventory: document.getElementById("mmInventory"),
    mmInventoryLimit: document.getElementById("mmInventoryLimit"),
    mmStatus: document.getElementById("mmStatus"),
    mmQuotes: document.getElementById("mmQuotes"),
};

// ── Clock ──
function updateClock() {
    const now = new Date();
    dom.clock.textContent = now.toISOString().substring(11, 23);
    requestAnimationFrame(updateClock);
}
updateClock();

// ── Pause/Resume ──
dom.pauseResumeButton?.addEventListener("click", () => {
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
    if (!dom.orderFlow) {
        return;
    }

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

initializeDemo();

// ── Order ticket ──
dom.resetDemoButton?.addEventListener("click", () => resetDemo());
dom.marketMakerToggle?.addEventListener("change", () => renderMarketMakerState(state.latestMarketMakerState));

dom.buyEsButton?.addEventListener("click", async () => {
    if (isBuyLimitReached()) {
        updateBuyButtonState();
        return;
    }

    await submitOrder({
        symbol: "ES",
        side: "BUY",
        quantity: state.manualOrderQuantity,
        orderType: "Market",
        owner: "Manual",
        useMarketMakerLiquidity: dom.marketMakerToggle?.checked ?? false,
    });
});

async function initializeDemo() {
    await resetDemo({ automatic: true });
    await start();
}

async function resetDemo({ automatic = false } = {}) {
    try {
        state.demoResetInFlight = true;
        if (dom.resetDemoButton) {
            dom.resetDemoButton.disabled = true;
            dom.resetDemoButton.textContent = automatic ? "RESETTING..." : "RESETTING";
        }

        const response = await fetch("/api/demo/reset", { method: "POST" });
        if (!response.ok) throw new Error(`Reset failed: ${response.status}`);
        resetDemoUi();
        await refreshTradingPanels();
    } catch (err) {
        console.error("Demo reset failed:", err);
        addLifecycleMessage("Rejected", err.message || "Demo reset failed");
    } finally {
        state.demoResetInFlight = false;
        if (dom.resetDemoButton) {
            dom.resetDemoButton.disabled = false;
            dom.resetDemoButton.textContent = "RESET DEMO";
        }
        updateBuyButtonState();
    }
}

function resetDemoUi() {
    state.currentPosition = 0;
    state.lastConsumedFills = [];
    state.latestLatency = null;
    state.latestSlippage = null;
    state.consumedFillIds.clear();
    state.lifecycleEventIds.clear();
    setText(dom.consumedLiquidity, "Recent consumed: —");
    setText(dom.avgFillBadge, "FILL AVG —");
    renderPosition(null);
    renderExecutionStats({ ordersSent: 0, ordersFilled: 0, cancels: 0, fillRatio: 0, pnl: 0 });
    resetSlippageStats();
    resetLatencyStats();
    if (dom.orderLifecycle) {
        dom.orderLifecycle.innerHTML = `<div class="lifecycle-row muted">Ready — submit BUY 100 ES @ MKT</div>`;
    }
    if (dom.openOrders) {
        dom.openOrders.textContent = "No recent orders";
    }
}

function resetSlippageStats() {
    setText(dom.slipArrival, "—");
    setText(dom.slipValue, "—");
    dom.slipValue?.classList.remove("positive", "negative");
}

function resetLatencyStats() {
    setText(dom.latTotal, "0ms");
    setText(dom.latRisk, "0ms");
    setText(dom.latRoute, "0ms");
    setText(dom.latExchange, "0ms");
    setText(dom.latFill, "0ms");
    setText(dom.latOther, "0ms");
}

async function submitOrder(order) {
    try {
        state.orderSubmitInFlight = true;
        updateBuyButtonState();
        const response = await fetch("/api/orders", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(order),
        });
        if (!response.ok) throw new Error(`Order request failed: ${response.status}`);
        renderOrderResult(await response.json());
        await refreshTradingPanels();
    } catch (err) {
        console.error("Order submit failed:", err);
        addLifecycleMessage("Rejected", err.message || "Order submit failed");
    } finally {
        state.orderSubmitInFlight = false;
        updateBuyButtonState();
    }
}

function renderOrderResult(result) {
    appendLifecycle(result.lifecycleEvents ?? []);
    if (result.depth) renderDepth(result.depth, result.consumedLiquidity ?? result.fills ?? []);
    if (result.position) renderPosition(result.position);
    if (result.slippage) renderSlippageStats(result.slippage);
    if (result.stats) renderExecutionStats(result.stats, result.latency);
}

async function refreshTradingPanels() {
    await Promise.all([
        refreshDepth(),
        refreshPositions(),
        refreshOpenOrders(),
        refreshExecutionStats(),
        refreshMarketMakerState(),
        refreshLifecycle(),
    ]);
}

async function refreshDepth() {
    if (!dom.depthBook) return;
    const response = await fetch("/api/depth/ES");
    if (response.ok) renderDepth(await response.json());
}

async function refreshPositions() {
    const response = await fetch("/api/positions");
    if (!response.ok) return;
    const positions = await response.json();
    renderPosition((positions ?? []).find((p) => p.symbol === "ES") ?? null);
    updateBuyButtonState();
}

async function refreshOpenOrders() {
    if (!dom.openOrders) return;
    const response = await fetch("/api/orders");
    if (response.ok) renderRecentOrders(await response.json());
}

async function refreshExecutionStats() {
    const response = await fetch("/api/execution-stats");
    if (response.ok) renderExecutionStats(await response.json());
}

async function refreshMarketMakerState() {
    const response = await fetch("/api/market-maker/state");
    if (response.ok) renderMarketMakerState(await response.json());
}

async function refreshLifecycle() {
    if (!dom.orderLifecycle || state.lifecycleEventIds.size > 0) return;
    const response = await fetch(`/api/lifecycle/recent?count=${state.maxLifecycleRows}`);
    if (response.ok) appendLifecycle(await response.json());
}

function renderDepth(depth, fills = []) {
    if (!dom.depthBook || !depth) return;
    appendConsumedFills(fills);
    const consumed = aggregateConsumedFills();

    renderConsumedLiquidity(consumed);
    const askRows = [...(depth.asks ?? [])].reverse().map((level) => depthRow(level, "ask", consumed));
    const midRow = `<div class="depth-row mid">MID ${formatPrice(depth.midPrice)}</div>`;
    const bidRows = (depth.bids ?? []).map((level) => depthRow(level, "bid", consumed));
    dom.depthBook.innerHTML = [...askRows, midRow, ...bidRows].join("");
    setText(dom.avgFillBadge, `FILL AVG ${formatPrice(weightedAverageFill() ?? depth.midPrice)}`);
}

function appendConsumedFills(fills) {
    for (const fill of fills ?? []) {
        const id = fill.fillId ?? `${fill.orderId}-${fill.price}-${fill.quantity}-${fill.timestamp}`;
        if (state.consumedFillIds.has(id)) continue;
        state.consumedFillIds.add(id);
        state.lastConsumedFills.unshift(fill);
    }

    state.lastConsumedFills = state.lastConsumedFills.slice(0, state.maxConsumedFills);
    state.consumedFillIds = new Set(state.lastConsumedFills.map((fill) => fill.fillId ?? `${fill.orderId}-${fill.price}-${fill.quantity}-${fill.timestamp}`));
}

function aggregateConsumedFills() {
    const consumed = new Map();
    state.lastConsumedFills.forEach((fill) => {
        const key = Number(fill.price).toFixed(2);
        consumed.set(key, (consumed.get(key) ?? 0) + fill.quantity);
    });
    return consumed;
}

function weightedAverageFill() {
    const totalQuantity = state.lastConsumedFills.reduce((sum, fill) => sum + Number(fill.quantity), 0);
    if (totalQuantity === 0) return null;
    const notional = state.lastConsumedFills.reduce((sum, fill) => sum + Number(fill.quantity) * Number(fill.price), 0);
    return notional / totalQuantity;
}

function depthRow(level, side, consumed) {
    const key = Number(level.price).toFixed(2);
    const consumedQuantity = consumed.get(key) ?? 0;
    return `<div class="depth-row ${side}${consumedQuantity > 0 ? " consumed" : ""}">
        <span>${formatPrice(level.price)}</span>
        <span>${Number(level.quantity).toLocaleString()}</span>
        <span>${consumedQuantity > 0 ? `-${consumedQuantity}` : ""}</span>
    </div>`;
}

function renderConsumedLiquidity(consumed) {
    if (!dom.consumedLiquidity) return;
    if (state.lastConsumedFills.length === 0) {
        dom.consumedLiquidity.textContent = "Recent consumed: —";
        return;
    }

    const total = state.lastConsumedFills.reduce((sum, fill) => sum + Number(fill.quantity), 0);
    const byPrice = Array.from(consumed.entries()).map(([price, qty]) => `${qty} @ ${price}`).join(", ");
    dom.consumedLiquidity.textContent = `Recent consumed: ${total} total (${byPrice})`;
}

function appendLifecycle(events) {
    if (!dom.orderLifecycle || !events || events.length === 0) return;

    dom.orderLifecycle.querySelector(".lifecycle-row.muted")?.remove();
    for (const evt of events) {
        const id = evt.eventId ?? `${evt.orderId}-${evt.stage}-${evt.timestamp}`;
        if (state.lifecycleEventIds.has(id)) continue;
        state.lifecycleEventIds.add(id);
        dom.orderLifecycle.insertAdjacentHTML("beforeend", lifecycleRow(evt));
    }

    while (dom.orderLifecycle.children.length > state.maxLifecycleRows) {
        const row = dom.orderLifecycle.firstElementChild;
        const id = row?.dataset.eventId;
        if (id) state.lifecycleEventIds.delete(id);
        row?.remove();
    }
}

function addLifecycleMessage(stage, message) {
    if (!dom.orderLifecycle) return;
    dom.orderLifecycle.querySelector(".lifecycle-row.muted")?.remove();
    dom.orderLifecycle.insertAdjacentHTML("beforeend", lifecycleRow({ stage, message, timestamp: new Date().toISOString() }));
    while (dom.orderLifecycle.children.length > state.maxLifecycleRows) dom.orderLifecycle.firstElementChild.remove();
}

function lifecycleRow(evt) {
    const stageClass = String(evt.stage ?? "").toLowerCase().replace(/\s+/g, "-");
    const time = new Date(evt.timestamp).toISOString().substring(11, 23);
    const id = evt.eventId ?? `${evt.orderId ?? "local"}-${evt.stage}-${evt.timestamp}`;
    return `<div class="lifecycle-row ${stageClass}" data-event-id="${id}">
        <span class="lifecycle-time">${time}</span>
        <span class="lifecycle-stage">${evt.stage}</span>
        <span>${evt.message}</span>
    </div>`;
}

function renderPosition(position) {
    const empty = position == null;
    state.currentPosition = empty ? 0 : Number(position.quantity);
    setText(dom.positionQty, empty ? "—" : state.currentPosition.toLocaleString());
    setText(dom.positionAvg, empty ? "—" : formatPrice(position.averagePrice));
    setMoney(dom.positionRealized, empty ? null : position.realizedPnl);
    setMoney(dom.positionUnrealized, empty ? null : position.unrealizedPnl);
    updateBuyButtonState();
}

function updateBuyButtonState() {
    if (!dom.buyEsButton) return;

    if (state.demoResetInFlight) {
        dom.buyEsButton.disabled = true;
        dom.buyEsButton.textContent = "RESETTING DEMO...";
        return;
    }

    if (state.orderSubmitInFlight) {
        dom.buyEsButton.disabled = true;
        dom.buyEsButton.textContent = "SENDING BUY 100 ES...";
        return;
    }

    if (isBuyLimitReached()) {
        dom.buyEsButton.disabled = true;
        dom.buyEsButton.textContent = `POSITION LIMIT ${state.maxPosition.toLocaleString()}`;
        return;
    }

    dom.buyEsButton.disabled = false;
    dom.buyEsButton.textContent = "BUY 100 ES @ MKT";
}

function isBuyLimitReached() {
    return state.currentPosition + state.manualOrderQuantity > state.maxPosition;
}

function renderRecentOrders(orders) {
    if (!dom.openOrders) return;
    if (!orders || orders.length === 0) {
        dom.openOrders.textContent = "No recent orders";
        return;
    }
    dom.openOrders.innerHTML = orders.slice(0, 8).map((order) => {
        const quantity = Number(order.filledQuantity || order.quantity).toLocaleString();
        const price = order.averageFillPrice == null ? formatOrderPrice(order) : `avg ${formatPrice(order.averageFillPrice)}`;
        const action = isOpenOrder(order)
            ? `<button class="mini-action-button" type="button" data-cancel-order="${order.orderId}">Cancel</button>`
            : `<span></span>`;

        return `<div class="open-order-row">
            <span>${order.symbol}</span>
            <span>${order.side}</span>
            <span>${quantity} ${price}</span>
            <span>${order.status}</span>
            ${action}
        </div>`;
    }).join("");
    dom.openOrders.querySelectorAll("[data-cancel-order]").forEach((button) => {
        button.addEventListener("click", () => cancelOrder(button.dataset.cancelOrder));
    });
}

function formatOrderPrice(order) {
    return order.limitPrice == null ? "MKT" : formatPrice(order.limitPrice);
}

function isOpenOrder(order) {
    return Number(order.remainingQuantity) > 0 && ["New", "Accepted", "Working", "PartiallyFilled"].includes(order.status);
}

async function cancelOrder(orderId) {
    const response = await fetch(`/api/orders/${orderId}`, { method: "DELETE" });
    if (response.ok || response.status === 409) renderOrderResult(await response.json());
    await refreshTradingPanels();
}

function renderSlippageStats(slippage) {
    state.latestSlippage = slippage ?? null;
    if (!state.latestSlippage) {
        resetSlippageStats();
        return;
    }

    const points = Number(state.latestSlippage.slippagePoints);
    const dollars = Number(state.latestSlippage.slippageDollars);
    setText(dom.slipArrival, formatPrice(state.latestSlippage.arrivalPrice));
    setText(dom.slipValue, `${points >= 0 ? "+" : ""}${points.toFixed(2)} pts / ${dollars < 0 ? "-$" : "+$"}${Math.abs(dollars).toLocaleString(undefined, { maximumFractionDigits: 0 })}`);
    dom.slipValue?.classList.toggle("negative", points > 0);
    dom.slipValue?.classList.toggle("positive", points < 0);
}

function renderExecutionStats(stats, latency) {
    if (!stats) return;
    setText(dom.execOrdersSent, Number(stats.ordersSent).toLocaleString());
    setText(dom.execOrdersFilled, Number(stats.ordersFilled).toLocaleString());
    setText(dom.execCancels, Number(stats.cancels).toLocaleString());
    setText(dom.execFillRatio, `${Number(stats.fillRatio).toFixed(0)}%`);
    setMoney(dom.execPnl, stats.pnl ?? stats.pnL);

    if (latency) {
        state.latestLatency = latency;
    } else if (Number(stats.ordersSent) === 0) {
        state.latestLatency = null;
    }

    renderLatencyStats(state.latestLatency);
}

function renderLatencyStats(latency) {
    if (!latency) {
        resetLatencyStats();
        return;
    }

    setText(dom.latTotal, `${Number(latency.totalMs).toFixed(1)}ms`);
    setText(dom.latRisk, `${Number(latency.riskCheckMs).toFixed(1)}ms`);
    setText(dom.latRoute, `${Number(latency.routeMs).toFixed(1)}ms`);
    setText(dom.latExchange, `${Number(latency.exchangeMs).toFixed(1)}ms`);
    setText(dom.latFill, `${Number(latency.fillMs).toFixed(1)}ms`);
    setText(dom.latOther, `${Number(latency.otherMs).toFixed(1)}ms`);
}

function renderMarketMakerState(mm) {
    if (!mm) return;
    state.latestMarketMakerState = mm;

    if (!(dom.marketMakerToggle?.checked ?? false)) {
        setText(dom.mmInventory, "—");
        setText(dom.mmInventoryLimit, Number(mm.inventoryLimit).toLocaleString());
        setText(dom.mmStatus, "OFF");
        setText(dom.mmQuotes, "—/—");
        dom.mmStatus?.classList.remove("danger", "warning");
        return;
    }

    setText(dom.mmInventory, Number(mm.inventory).toLocaleString());
    setText(dom.mmInventoryLimit, Number(mm.inventoryLimit).toLocaleString());
    setText(dom.mmStatus, mm.status);
    setText(dom.mmQuotes, `${mm.bidEnabled ? "BID" : "—"}/${mm.askEnabled ? "ASK" : "—"}`);
    const statusClass = Math.abs(Number(mm.inventory)) >= Number(mm.inventoryLimit) ? "danger" : Math.abs(Number(mm.inventory)) >= 30 ? "warning" : "";
    dom.mmStatus?.classList.toggle("danger", statusClass === "danger");
    dom.mmStatus?.classList.toggle("warning", statusClass === "warning");
}

function setText(el, text) {
    if (el) el.textContent = text;
}

function setMoney(el, value) {
    if (!el) return;
    if (value == null) {
        el.textContent = "—";
        el.classList.remove("positive", "negative");
        return;
    }
    const amount = Number(value);
    el.textContent = `${amount < 0 ? "-$" : "$"}${Math.abs(amount).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
    el.classList.toggle("positive", amount > 0);
    el.classList.toggle("negative", amount < 0);
}

refreshTradingPanels();
setInterval(refreshTradingPanels, 2000);

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
