# Trade Demo Architecture

## System Overview

This repository implements a .NET 8 trading-infrastructure demo with three cooperating client/server pieces:

1. **ASP.NET Core API host** — serves the static browser terminal, exposes REST APIs, hosts the SignalR hub, runs background market-data services, owns the in-memory exchange simulator, and optionally writes/reads a durable tick journal.
2. **Browser trading terminal** — static HTML/CSS/vanilla JavaScript under `src/TradeDemo.Api/wwwroot`, using SignalR for live market-data snapshots and REST for order, replay, depth, metrics, and position views.
3. **WPF trading terminal** — a .NET 8 Windows desktop client under `src/TradeDemo.Wpf`, using the same SignalR hub and REST API as the browser UI.

The demo intentionally separates high-volume market data from order/execution commands:

- **Market-data path** — synthetic ticks are generated at very high rates, published through an in-process bus, stored in a recent replay ring plus optional journal, and coalesced into browser/desktop-friendly SignalR snapshots.
- **Order/execution path** — REST order mutations are submitted into a bounded FIFO order-command queue, serialized by a hosted command loop, risk-checked, simulated against a stateful synthetic depth book, reflected into positions/PnL, and returned as API responses.

The goal is not to implement a real exchange, production OMS, or full matching engine. The goal is to demonstrate realistic trading-system mechanics: high-throughput streaming, backpressure, coalescing, replay, order lifecycle, pre-trade risk, synthetic liquidity consumption, partial fills, inventory risk, and observable latency/queue metrics.

```text
Browser Terminal / WPF Terminal

├── SignalR /tradehub
│   ├── TradeSignals snapshots
│   ├── Stats
│   └── ReplayStateChanged
│
└── REST APIs
    ├── market data, metrics, queue stats, and replay
    ├── order submit/cancel/modify and order queue stats
    ├── executions, lifecycle, trade monitor, and execution stats
    ├── depth, positions, and market-maker state
    └── demo reset

ASP.NET Core API Host

├── MarketDataSimulator
│   └── InMemoryMarketDataBus
│       ├── LosslessTickStore → recent ring buffer + optional journal
│       └── TradeQueueProcessor → coalesced SignalR UI stream
│
├── ReplayEngine
│   ├── scenario replay
│   ├── recent tick replay
│   └── journal replay
│
└── Order/Execution Simulation
    ├── REST endpoints in Program.cs
    └── OrderCommandQueue → OrderCommandProcessor
        └── ExchangeOrderCommandExecutor
            └── ExchangeSimulator
                ├── RiskEngine
                ├── stateful synthetic DOM
                ├── order lifecycle + execution reports
                ├── partial fills / open orders / cancel / modify
                ├── market-maker inventory state
                └── PositionManager → realized + unrealized PnL
```

## Important Naming Boundaries

This project uses trading-system terminology, but the boundaries are deliberate:

| Term | Current implementation |
|---|---|
| Frontend | Static browser terminal plus WPF desktop terminal; no React/Angular SPA. |
| SignalR | Used for live market-data snapshots, server stats, and replay state notifications; order submission remains REST. |
| Market-data bus | In-process fan-out via `IMarketDataBus`/`IMarketDataSubscriber`, not Kafka/EventStore. |
| Order gateway | Minimal API endpoints plus `OrderCommandQueue`; there is no separate deployable gateway service. |
| Order command queue | In-process bounded `Channel<QueuedOrderCommand>` with `FullMode=Wait`, not Azure Service Bus. |
| Exchange simulator | In-memory simulation of lifecycle, risk, synthetic DOM consumption, partial fills, resting demo limit orders, cancel, and modify. |
| Matching engine | Not a real exchange-grade matching engine. The UI may label the demonstration as FIFO price-time, but there is no real multi-participant price-time-priority book or queue-position model. |
| Event stream | Represented by SignalR messages, order lifecycle events, execution reports, and API responses; no standalone event bus. |
| PnL service | PnL is derived inside `PositionManager`; there is no separate PnL microservice. |
| Persistence | Tick journal is optional; orders/executions/positions are in-memory only. |

## Market-Data Pipeline

### Producer: `MarketDataSimulator`

`MarketDataSimulator` is a `BackgroundService` optimized for very high synthetic throughput. It continuously generates `TradeSignal` records across equities, crypto, and futures-style instruments using random-walk prices, burst intensity, preallocated symbol/exchange strings, batched timestamps, and monotonic sequence IDs.

Each generated signal is published once:

```text
MarketDataSimulator
    ↓ IMarketDataBus.Publish(signal)
InMemoryMarketDataBus
    ├── LosslessTickStore.OnMarketData(signal)
    └── TradeQueueProcessor.OnMarketData(signal)
```

This split is important:

- The **authoritative/replay path** accepts ticks into `LosslessTickStore` for sequence-aware recent replay and optional durable journal writes.
- The **UI path** accepts ticks into `TradeQueueProcessor` for latest-by-symbol coalescing and SignalR broadcast.

### In-Process Fan-Out: `InMemoryMarketDataBus`

`InMemoryMarketDataBus` is a small in-process pub/sub component. It is registered as `IMarketDataBus` and receives the fixed subscriber set from dependency injection:

```text
IMarketDataSubscriber[]
├── LosslessTickStore
└── TradeQueueProcessor
```

Subscribers own their own channel and backpressure semantics, so the producer path does not need to know whether a consumer is replay-oriented or UI-oriented.

### UI Stream: `TradeQueueProcessor`

`TradeQueueProcessor` is a `BackgroundService` and market-data subscriber. It owns a bounded `Channel<TradeSignal>` sized for bursty demo traffic:

| Setting | Current value |
|---|---|
| Channel capacity | `100_000` |
| Full mode | `DropOldest` |
| Single reader | `true` |
| Single writer | `false` |
| Broadcast cadence | about every `33ms` |
| Stats cadence | about every `500ms` |

The channel uses `DropOldest` because stale quotes are less useful than the newest quote in a real-time terminal.

```text
MarketDataSimulator
    ↓ Publish
InMemoryMarketDataBus
    ↓ OnMarketData / TryEnqueue
Bounded Channel<TradeSignal>
    ↓ drain on timer tick
Latest-by-symbol dictionary
    ↓ SignalR SendAsync("TradeSignals", snapshot)
Browser and WPF clients
```

Key mechanics:

- bounded channel backpressure with stale-tick discard
- single-reader drain loop
- multiple producer support
- latest-by-symbol coalescing
- snapshot broadcast near monitor refresh rates
- `Stats` SignalR messages with processed/dropped/coalesced/broadcast metrics
- interlocked counters on the hot path
- latency and byte metrics recorded through `PerformanceMetrics`

The clients do **not** receive every raw tick. They receive compact snapshots that keep the UI responsive while the server continues processing high raw event rates.

### Lossless Recent Store + Optional Journal

`LosslessTickStore` is also a `BackgroundService` and market-data subscriber. It owns its own bounded channel and keeps a large recent replay window.

| Setting | Current value |
|---|---|
| Channel capacity | `1_000_000` |
| Full mode | `Wait` for async writes; `TryAppend` counts failures if immediate write cannot be accepted |
| Recent tick capacity | `500_000` |
| Journal batch size | configured by `TickJournal:BatchSize` (default `4096`) |
| Journal flush interval | configured by `TickJournal:FlushIntervalMilliseconds` (default `250ms`) |

It tracks accepted count, dropped count, sequence gaps, last sequence ID, queue depth, and recent-window size. The word “lossless” describes the intent of the authoritative path, but the current implementation can count drops if the in-memory channel cannot immediately accept a tick under extreme saturation.

Supported journal interfaces:

```text
ITickJournalWriter
ITickJournalReader
ITickJournal
```

Implemented providers:

- `NullTickJournal` when journaling is disabled or provider is `None`
- local segment files
- Azure Blob-backed journal
- Azure Event Hubs-backed journal adapter

The default `appsettings.json` has journaling disabled:

```json
"TickJournal": {
  "Enabled": false,
  "Provider": "None"
}
```

## Replay Architecture

`ReplayEngine` supports one replay at a time and reports state through both REST and SignalR `ReplayStateChanged` messages.

### 1. Scenario Replay

Synthetic traffic profiles demonstrate backpressure and UI behavior:

| Scenario | Purpose |
|---|---|
| Calm Market | Baseline steady-state behavior |
| NASDAQ Burst | Burst traffic and queue pressure |
| Flash Crash | Volatility spike and recovery behavior |
| Ramp to Saturation | Increasing load and queue saturation |
| Exchange Disconnect | Simulated feed pause/reconnect behavior |

Scenario replay generates fresh synthetic ticks, appends them to `LosslessTickStore`, and enqueues them into the UI pipeline. Historical recent/journal replay uses the UI pipeline only to avoid duplicating already-stored ticks.

### 2. Historical Tick Replay

Replay can also read from:

- recent in-memory tick log
- journal by sequence ID

```text
POST /api/replay/recent?count=10000&speed=1
POST /api/replay/journal/from/{sequenceId}?count=10000&speed=1
```

Historical replay sends ticks back into `TradeQueueProcessor` only. It deliberately does not re-append replayed ticks into `LosslessTickStore`, because the authoritative store or journal already contains those events.

## Order and Execution Simulation

The order path is REST-driven and separate from the SignalR market-data stream.

### Command Queue Flow

Submit, cancel, and modify requests are serialized through `OrderCommandQueue`:

```text
Browser / WPF
    ↓ POST /api/orders, DELETE /api/orders/{id}, PUT /api/orders/{id}
Minimal API endpoint
    ↓ EnqueueAsync(command)
Bounded Channel<QueuedOrderCommand>
    ↓ single reader
OrderCommandProcessor BackgroundService
    ↓ DispatchAsync
ExchangeOrderCommandExecutor
    ↓
ExchangeSimulator
```

`OrderCommandQueue` uses a bounded `Channel<QueuedOrderCommand>` with:

| Setting | Current value |
|---|---|
| Capacity | `4_096` |
| Full mode | `Wait` |
| Single reader | `true` |
| Single writer | `false` |

Each API caller awaits the command completion task. The queue records enqueued/dequeued/processed counts, failed/canceled counts, average/max queue wait, average/max processing time, current depth, and timestamp. Stats are exposed at:

```text
GET /api/orders/queue/stats
```

### Execution Flow

The current logical execution flow is:

```text
OrderCommandProcessor
    ↓
ExchangeSimulator.SubmitOrderAsync
    ↓ normalize order + snapshot pre-trade depth
RiskEngine.Check
    ↓
simulated route / exchange / fill hops
    ↓
stateful synthetic depth consumption or resting limit order
    ↓
ExecutionReport(s) + OrderLifecycleEvent(s)
    ↓
PositionManager.ApplyFills
    ↓
OrderResult returned to API caller
```

Cancel and modify commands are also serialized through the command queue. They update in-memory order state and execution/lifecycle collections when applicable.

### Order Lifecycle Events

Each order returns timestamped lifecycle events, for example:

```text
09:30:00.123 Submitted          Order Submitted BUY 100 ES @ Market
09:30:00.126 Risk Check Passed  Risk Check Passed (0.8ms)
09:30:00.131 Routed             Routed to CME (2.4ms)
09:30:00.143 Accepted           Accepted (7.1ms)
09:30:00.151 Fill               Filled 30 @ 5982.25
09:30:00.156 Fill               Filled 45 @ 5982.50
09:30:00.162 Fill               Filled 25 @ 5982.75
09:30:00.163 Filled             Order Fully Filled (31.4ms)
```

Lifecycle events are returned in `OrderResult` and can also be read via:

```text
GET /api/lifecycle/recent?count=80
```

They are not currently pushed through SignalR as a separate order event bus.

### Synthetic Depth-of-Market

`ExchangeSimulator` owns a stateful synthetic depth book per symbol. ES and NQ use futures-like tick handling and a contract multiplier in PnL calculations; ES depth uses `0.25` tick size.

Example pre-trade book:

```text
ASK 5982.75  25
ASK 5982.50  45
ASK 5982.25  30
---- MID 5982.00 ----
BID 5981.75  60
BID 5981.50  40
```

A `BUY 100 ES @ Market` consumes ask-side liquidity:

```text
30 @ 5982.25
45 @ 5982.50
25 @ 5982.75
```

Then consumed levels disappear and the book replenishes farther away. This demonstrates:

- liquidity consumption
- partial fills
- slippage
- market impact
- average fill price
- order lifecycle reporting

It is still not a real matching engine. There is no true external participant model, queue position, self-match prevention, persistence, or production-grade price-time priority.

### Resting Limit Orders, Cancel, and Modify

Limit orders that cannot immediately cross the synthetic book can rest as `Working` orders in the in-memory order list. The API supports:

```text
GET    /api/orders/open
DELETE /api/orders/{orderId}
PUT    /api/orders/{orderId}
```

Cancel and modify operations are routed through the same FIFO command queue, which keeps demo state transitions deterministic.

## Risk Controls

`RiskEngine` performs pre-trade checks before an order is routed/accepted:

| Check | Behavior |
|---|---|
| Quantity check | Rejects non-positive quantity |
| Side check | Allows only `BUY` or `SELL` |
| Max order quantity | Rejects quantity above `1_000` |
| Max notional | Rejects notional above `10,000,000` |
| Fat-finger band | Rejects limit prices more than 5% away from reference price |
| Position limit | Rejects orders that would breach absolute position of `1_000` |
| Demo venue throttle | Rare low-rate synthetic reject for small demo orders |

Example rejects:

```text
Rejected - Position Limit Exceeded (1,000)
Rejected - Fat Finger Check - limit price outside 5% reference band
Rejected - Risk Limit Breach - simulated venue throttle
```

Rejected orders appear as rejected lifecycle/execution rows in the UI.

## Position and PnL

`PositionManager` applies fills incrementally and is protected by a lock around its position dictionary operations.

```text
Fill(s)
    ↓
PositionManager.ApplyFills / ApplyFill
    ↓
quantity + average price + realized PnL + mark price + unrealized PnL
```

It tracks:

- signed quantity
- average price
- mark price
- realized PnL
- derived unrealized PnL
- update time

PnL is currently derived from in-memory position state. Futures-style symbols such as ES and NQ use a contract multiplier of `50`; other symbols use `1`. There is no separate PnL service and no fees/commissions model.

## Market Maker Demo

The UI includes an optional market-maker demonstration. It is intentionally demo-oriented rather than a backend hosted strategy service.

The browser-side market-maker mode periodically:

1. fetches `/api/depth/ES`
2. fetches `/api/market-maker/state`
3. submits small bid/ask limit orders if inventory risk allows
4. occasionally submits a tiny market order to demonstrate inventory/PnL movement

Market-maker risk state is exposed by:

```text
GET /api/market-maker/state
```

The state includes inventory, inventory limit, status, bid/ask enablement, and related display fields. `ExchangeSimulator` updates market-maker inventory from fills and disables one side as inventory approaches configured thresholds.

## Client Applications

### Browser Terminal

The browser terminal is served directly by the ASP.NET Core app from `wwwroot`. It uses:

- `index.html` for the live trading terminal
- `architecture.html`, `performance.html`, `concurrency.html`, and `replay.html` for explainer pages
- `js/terminal.js` for SignalR, REST polling, order submission, depth, lifecycle, market-maker demo, and UI updates
- CSS under `wwwroot/css`

### WPF Terminal

The WPF terminal is a separate .NET 8 Windows desktop project that talks to the same backend:

```text
MainWindow / MainWindowViewModel
├── TradeHubClient       → SignalR /tradehub
├── TradeApiClient       → REST APIs
├── TerminalFeedProcessor → client-side coalescing/drain queue
└── DispatcherTimer loops → render, clock, stats, and message-rate updates
```

It shows live prices, ticker/feed rows, order-flow aggregation, depth, lifecycle, trade monitor, positions, market-maker state, execution stats, and system metrics. It submits demo market buy orders and can reset demo state through REST.

## Latency and Metrics

The market-data path records server-side latency and byte estimates around SignalR snapshot broadcast. The order path returns simulated latency components around risk, route, exchange, and fill stages.

Example order latency breakdown:

```text
Risk Check     0.8ms
Route          2.4ms
Exchange       7.1ms
Fill          31.4ms
Total         41.7ms
```

These are demo timings measured around simulated `Task.Delay` hops and deterministic latency functions. They are useful for explaining order-path mechanics, not for claiming production exchange latency.

## API Surface

### Market Data / Metrics

| Endpoint | Description |
|---|---|
| `GET /api/health` | Basic health check |
| `GET /api/metrics` | Performance snapshot |
| `GET /api/metrics/stream` | SSE metrics stream |
| `GET /api/queue/stats` | Market-data queue/coalescing/lossless stats |
| `GET /api/orders/queue/stats` | Order-command queue stats |
| `GET /api/concurrency/threads` | Demo thread/rate breakdown for concurrency page |
| `GET /api/ticks/recent` | Recent in-memory tick log |
| `GET /api/ticks/from/{sequenceId}` | Recent ticks from sequence |
| `GET /api/ticks/stats` | Lossless tick store stats |
| `GET /api/system/metrics` | CPU/memory/thread metrics snapshot |

### Replay

| Endpoint | Description |
|---|---|
| `GET /api/replay/scenarios` | Available replay scenarios |
| `POST /api/replay/start` | Start a scenario replay |
| `POST /api/replay/recent` | Replay recent ticks |
| `POST /api/replay/journal/from/{sequenceId}` | Replay journal ticks from sequence |
| `POST /api/replay/stop` | Stop replay |
| `GET /api/replay/state` | Current replay state |

### Order / Execution

| Endpoint | Description |
|---|---|
| `POST /api/orders` | Submit an order through the command queue |
| `GET /api/orders` | All known orders |
| `GET /api/orders/open` | Working/open orders |
| `DELETE /api/orders/{orderId}` | Cancel an open order through the command queue |
| `PUT /api/orders/{orderId}` | Modify an open order through the command queue |
| `GET /api/executions` | Execution reports |
| `GET /api/lifecycle/recent` | Recent order lifecycle events |
| `GET /api/execution-stats` | Order/fill/cancel/PnL statistics |
| `GET /api/trade-monitor` | Trade-monitor rows for terminal UI |
| `GET /api/depth/{symbol}` | Current synthetic DOM snapshot |
| `GET /api/positions` | Current positions |
| `GET /api/market-maker/state` | Inventory-risk state for market-maker demo |
| `POST /api/demo/reset` | Reset in-memory demo trading state |

## Concurrency Model

| Component | Model | Notes |
|---|---|---|
| `MarketDataSimulator` | `BackgroundService` | Generates synthetic ticks continuously and publishes to `IMarketDataBus` |
| `InMemoryMarketDataBus` | singleton fan-out | Synchronously invokes fixed in-process subscribers |
| `LosslessTickStore` | `BackgroundService` + bounded channel | Stores recent accepted ticks and optional journal batches |
| `TradeQueueProcessor` | `BackgroundService` + bounded channel | Coalesces latest symbol snapshots for SignalR |
| `ReplayEngine` | async tasks + cancellation token | Runs one replay at a time and publishes replay state |
| `OrderCommandQueue` | bounded channel | Serializes submit/cancel/modify commands with `FullMode=Wait` |
| `OrderCommandProcessor` | `BackgroundService` | Single-reader FIFO command dispatch loop |
| `ExchangeSimulator` | singleton + locks | Protects in-memory orders, executions, lifecycle, telemetry, market-maker inventory, and synthetic DOM |
| `PositionManager` | singleton + locks | Protects position state; computes realized/unrealized PnL |
| `TradeHub` | SignalR hub | Supports connection logging and symbol groups; broadcasts are currently sent to all clients by the processor |
| Browser terminal | SignalR + REST + timers | Coalesced snapshots plus REST polling/actions |
| WPF terminal | SignalR + REST + dispatcher timers | Client-side queue/drain to keep UI rendering bounded |

The market-data path uses separate channels for authoritative storage and UI coalescing. The order path now also uses a channel: a bounded FIFO command queue that serializes mutating order operations before they reach the exchange simulator.

## Deployment Topology

The API app is designed to run locally or as a containerized ASP.NET Core service on Azure Container Apps. The WPF client is a separate desktop executable that can point at the hosted API URL.

```text
Azure Container Apps or local Kestrel
├── ASP.NET Core API
├── Static browser terminal
├── SignalR hub
├── REST APIs
├── background market-data services
├── in-memory order/exchange/position state
└── optional tick journal adapters

Optional Azure integrations
├── Blob Storage journal
├── Event Hubs journal adapter
├── Container Registry
└── Log Analytics

Desktop client
└── WPF terminal → same public API + SignalR hub
```

## What This Demo Demonstrates Well

- high-throughput synthetic market-data generation
- in-process market-data fan-out to multiple consumers
- bounded-channel backpressure with different policies per path
- coalesced SignalR UI snapshots
- sequence IDs and replay-oriented tick storage
- optional local/Azure tick journaling
- deterministic replay scenarios and historical replay
- REST order submission/cancel/modify
- FIFO in-process order command queue
- pre-trade risk checks
- stateful synthetic DOM liquidity consumption
- partial fills and average fill price
- resting demo limit orders
- order lifecycle, execution reports, and latency breakdown
- position updates with realized and unrealized PnL
- market-maker inventory-risk display
- browser and WPF clients sharing the same backend

## What This Demo Intentionally Does Not Implement

- real exchange connectivity
- FIX protocol
- real exchange-grade matching engine
- real price-time-priority multi-participant book
- order queue position or self-match prevention
- separate deployable OMS/EMS/gateway/PnL services
- Kafka/EventStore event bus
- persistent order/execution/position database
- multi-account permissions
- production-grade risk/compliance controls
- fees, commissions, corporate actions, or account-level margin

These boundaries are intentional so the project remains a focused portfolio demo rather than an incomplete production trading system.
