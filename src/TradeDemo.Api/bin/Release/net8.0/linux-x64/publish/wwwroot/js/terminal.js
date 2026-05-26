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
    renderBatchSize: 140,
    pendingSignals: [],
    renderScheduled: false,
    tickerDirty: false,
    lastSignalAt: Date.now(),
    reconnectInFlight: false,
    reconnectRetryHandle: null,
};

// ── DOM refs ──
const dom = {
    connectionDot: document.getElementById("connectionDot"),
    connectionStatus: document.getElementById("connectionStatus"),
    throughputBadge: document.getElementById("throughputBadge"),
    queueDepthBadge: document.getElementById("queueDepthBadge"),
    clock: document.getElementById("clock"),
    tickerTape: document.getElementById("tickerTape"),
    priceGrid: document.getElementById("priceGrid"),
    signalFeed: document.getElementById("signalFeed"),
    orderFlow: document.getElementById("orderFlow"),
    feedCount: document.getElementById("feedCount"),
    statProcessed: document.getElementById("statProcessed"),
    statDropped: document.getElementById("statDropped"),
    statQueueDepth: document.getElementById("statQueueDepth"),
    statMsgSec: document.getElementById("statMsgSec"),
};

// ── Clock ──
function updateClock() {
    const now = new Date();
    dom.clock.textContent = now.toISOString().substring(11, 23);
    requestAnimationFrame(updateClock);
}
updateClock();

// ── Throughput meter ──
setInterval(() => {
    const elapsed = Math.max(0.001, (Date.now() - state.lastMsgCountReset) / 1000);
    state.msgPerSec = Math.round(state.msgCount / elapsed);
    dom.throughputBadge.textContent = `${state.msgPerSec} msg/s`;
    dom.statMsgSec.textContent = state.msgPerSec.toLocaleString();
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

connection.on("Stats", (stats) => {
    dom.statProcessed.textContent = Number(stats.processedTotal).toLocaleString();
    dom.statDropped.textContent = Number(stats.droppedTotal).toLocaleString();
    dom.statQueueDepth.textContent = Number(stats.queueDepth).toLocaleString();
    dom.queueDepthBadge.textContent = `Q: ${stats.queueDepth}`;
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
    if (state.renderScheduled) {
        return;
    }

    state.renderScheduled = true;
    requestAnimationFrame(drainSignals);
}

function drainSignals() {
    state.renderScheduled = false;

    if (state.pendingSignals.length === 0) {
        return;
    }

    const latestBySymbol = new Map();
    const feedSlice = [];

    let processed = 0;
    while (state.pendingSignals.length > 0 && processed < state.renderBatchSize) {
        const signal = state.pendingSignals.shift();
        processed++;

        state.msgCount++;
        state.signalCount++;

        latestBySymbol.set(signal.symbol, signal);
        accumulateOrderFlow(signal);

        // Keep only latest rows for feed insertion this frame.
        feedSlice.push(signal);
        if (feedSlice.length > 24) {
            feedSlice.shift();
        }
    }

    latestBySymbol.forEach((signal) => {
        updatePriceGrid(signal);
        state.tickerData.set(signal.symbol, signal);
    });

    for (let i = feedSlice.length - 1; i >= 0; i--) {
        addSignalRow(feedSlice[i]);
    }

    renderOrderFlow();

    dom.feedCount.textContent = state.signalCount.toLocaleString();
    state.tickerDirty = true;

    // If backlog remains, process on next animation frame without locking main thread.
    if (state.pendingSignals.length > 0) {
        scheduleRender();
    }
}

// ── Price Grid ──
function updatePriceGrid(signal) {
    let row = document.getElementById(`price-${signal.symbol}`);
    const isUp = signal.change >= 0;

    if (!row) {
        row = document.createElement("div");
        row.className = "price-row";
        row.id = `price-${signal.symbol}`;
        row.innerHTML = `
            <span class="price-symbol"></span>
            <span class="price-value"></span>
            <span class="price-change"></span>
            <span class="price-exchange"></span>
        `;
        dom.priceGrid.appendChild(row);
    }

    row.querySelector(".price-symbol").textContent = signal.symbol;
    row.querySelector(".price-exchange").textContent = signal.exchange;

    const valueEl = row.querySelector(".price-value");
    valueEl.textContent = formatPrice(signal.price);
    valueEl.className = `price-value ${isUp ? "ticker-up" : "ticker-down"}`;

    const changeEl = row.querySelector(".price-change");
    changeEl.textContent = `${isUp ? "+" : ""}${signal.changePercent.toFixed(2)}%`;
    changeEl.className = `price-change ${isUp ? "ticker-up" : "ticker-down"}`;

    // Lightweight flash without forcing synchronous layout.
    row.classList.remove("flash-green", "flash-red");
    queueMicrotask(() => row.classList.add(isUp ? "flash-green" : "flash-red"));
    setTimeout(() => row.classList.remove("flash-green", "flash-red"), 120);

    state.prices.set(signal.symbol, signal);
}

// ── Signal Feed ──
function addSignalRow(signal) {
    const row = document.createElement("div");
    const isBuy = signal.direction === "BUY";
    row.className = `signal-row ${isBuy ? "buy" : "sell"}`;

    const time = new Date(signal.timestamp).toISOString().substring(11, 23);
    row.innerHTML = `
        <span class="signal-time">${time}</span>
        <span class="signal-symbol">${signal.symbol}</span>
        <span class="signal-price ${isBuy ? "ticker-up" : "ticker-down"}">${formatPrice(signal.price)}</span>
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
            <span class="ticker-price ${cls}">${formatPrice(s.price)} ${arrow} ${Math.abs(s.changePercent).toFixed(2)}%</span>
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
        forceReconnect("stale-watchdog");
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
        await forceReconnect("visibility-resume");
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
