# Trade Demo Architecture

## System Overview

This repository implements a .NET 8 trading-infrastructure demo with two intentionally separate paths:

1. **Market-data path** — high-throughput synthetic ticks flow through bounded channels, coalesced SignalR snapshots, metrics, lossless recent storage, and optional durable tick journals.
2. **Order/execution path** — browser orders are submitted through REST APIs into an in-memory exchange simulator that performs risk checks, consumes a stateful synthetic depth-of-market book, emits lifecycle events and partial fills, updates positions/PnL, and exposes execution statistics.

The goal is not to implement a real exchange, production OMS, or full matching engine. The goal is to demonstrate realistic trading-system mechanics: streaming market data, backpressure, replay, order lifecycle, pre-trade risk, synthetic liquidity consumption, partial fills, inventory risk, and observable latency breakdowns.

```text
Browser Trading Terminal
(HTML/CSS/Vanilla JS)

├── SignalR /tradehub
│   └── Live market-data snapshots + server stats
│
└── REST APIs
    ├── /api/orders
    ├── /api/depth/{symbol}
    ├── /api/orders/open
    ├── /api/executions
    ├── /api/execution-stats
    ├── /api/positions
    └── /api/market-maker/state

ASP.NET Core API

├── MarketDataSimulator
│   ├── LosslessTickStore → recent ring buffer + optional journal
│   └── TradeQueueProcessor → coalesced SignalR UI stream
│
├── ReplayEngine
│   ├── scenario replay
│   ├── recent tick replay
│   └── journal replay
│
└── Order/Execution Simulation
    ├── RiskEngine
    ├── ExchangeSimulator
    │   ├── stateful synthetic DOM
    │   ├── order lifecycle events
    │   ├── partial fills
    │   ├── open orders / cancel / modify
    │   └── execution latency breakdown
    └── PositionManager
        └── position + derived unrealized PnL
```

## Important Naming Boundaries

This project uses trading-system terminology, but the boundaries are deliberate:

| Term | Current implementation |
|---|---|
| Frontend | Browser terminal built with static HTML/CSS/vanilla JavaScript, not React. |
| SignalR | Used for live market-data and stats streaming, not order submission. |
| Order API | REST endpoints in `Program.cs`; there is no separate order-gateway service class. |
| Exchange simulator | In-memory simulation of lifecycle, synthetic DOM consumption, partial fills, and resting demo limit orders. |
| Matching engine | Not a real matching engine. No real price-time priority book or market microstructure. |
| Event stream | Represented as order lifecycle events, execution reports, SignalR market-data events, and API responses. There is no standalone Kafka/EventStore event bus. |
| PnL service | PnL is derived from `PositionManager` state, not a separate service. |

## Market-Data Pipeline

### Producer: `MarketDataSimulator`

`MarketDataSimulator` is a background service that generates synthetic trade signals across equities, crypto, and futures-style instruments. Each `TradeSignal` includes:

- symbol
- bid/ask/mid prices
- price change
- volume
- exchange
- timestamp
- side/direction
- monotonic sequence ID

The simulator writes each generated signal to two paths:

```text
MarketDataSimulator
├── LosslessTickStore.TryAppend(signal)
└── TradeQueueProcessor.TryEnqueue(signal)
```

This split is important:

- The **lossless/replay path** aims to retain accepted ticks in sequence order for replay and journal persistence.
- The **UI path** is intentionally coalesced and browser-friendly.

### UI Stream: `TradeQueueProcessor`

`TradeQueueProcessor` owns a bounded `Channel<TradeSignal>` with drop-oldest behavior. This is appropriate for a real-time UI because stale quotes are less valuable than fresh quotes.

```text
MarketDataSimulator
    ↓ TryEnqueue
Bounded Channel<TradeSignal>
    ↓ drain loop
Latest-by-symbol snapshot
    ↓ SignalR
Browser clients
```

Key mechanics:

- bounded channel capacity
- single-reader drain loop
- multiple producer support
- latest-by-symbol coalescing
- SignalR broadcast cadence around UI refresh rates
- interlocked counters for processed/dropped/coalesced stats

The UI does **not** receive every raw tick. It receives compact snapshots that keep the browser responsive while the server continues processing high raw event rates.

### Lossless Recent Store + Journal

`LosslessTickStore` maintains an in-process recent tick ring buffer and optionally writes batches to a configured tick journal.

Supported journal interfaces:

```text
ITickJournalWriter
ITickJournalReader
ITickJournal
```

Implemented providers include:

- local segment files
- Azure Blob-backed journal
- Azure Event Hubs-backed journal adapter
- null journal

The recent in-memory store is useful for fast local replay. The journal abstraction is useful for durable replay scenarios. Under extreme saturation, the in-memory append path can count drops, so use the word “lossless” as the intent of the authoritative path rather than a guarantee under all overload conditions.

## Replay Architecture

`ReplayEngine` supports two classes of replay:

### 1. Scenario Replay

Synthetic traffic profiles demonstrate backpressure and UI behavior:

| Scenario | Purpose |
|---|---|
| Calm Market | Baseline steady-state behavior |
| NASDAQ Burst | Burst traffic and queue pressure |
| Flash Crash | Volatility spike and recovery behavior |
| Ramp to Saturation | Increasing load and queue saturation |
| Exchange Disconnect | UI reconnect/empty-window behavior |

### 2. Historical Tick Replay

Replay can also read from:

- recent in-memory tick log
- journal by sequence ID

```text
POST /api/replay/recent?count=10000&speed=1
POST /api/replay/journal/from/{sequenceId}?count=10000&speed=1
```

Journal replay sends historical ticks back into the UI pipeline without re-appending them to the authoritative tick store.

## Order and Execution Simulation

The order path is REST-driven and separate from the SignalR market-data stream.

```text
Browser Order Entry
    ↓ POST /api/orders
Order API
    ↓
ExchangeSimulator.SubmitOrderAsync
    ↓
RiskEngine.Check
    ↓
ExchangeSimulator stateful DOM
    ↓
Partial Fill(s)
    ↓
PositionManager.ApplyFill
    ↓
OrderResult returned to UI
```

### Order Lifecycle Events

Each order returns timestamped lifecycle events, for example:

```text
09:30:00.123 Submitted        Order Submitted BUY 100 ES @ Market
09:30:00.126 Risk Check Passed Risk Check Passed (3.1ms)
09:30:00.131 Routed           Routed to CME (5.4ms)
09:30:00.143 Accepted         Accepted (12.2ms)
09:30:00.151 Fill             Filled 30 @ 5982.25
09:30:00.156 Fill             Filled 45 @ 5982.50
09:30:00.162 Fill             Filled 25 @ 5982.75
09:30:00.163 Filled           Filled (19.5ms)
```

The lifecycle stream is exposed in the `OrderResult` response and rendered by the browser. It is not currently pushed through SignalR as a separate event bus.

### Synthetic Depth-of-Market

`ExchangeSimulator` owns a stateful synthetic depth book per symbol. For ES, it uses a tick size of `0.25`.

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

Then the consumed levels disappear and the book replenishes farther away:

```text
ASK 5983.25 100
ASK 5983.00  60
---- MID 5982.00 ----
BID 5981.75  60
BID 5981.50  40
```

This demonstrates:

- liquidity consumption
- partial fills
- slippage
- market impact
- average fill price

It is still not a real matching engine. There is no true price-time priority, queue position, self-match prevention, or external market participant model.

### DOM Ticking

The synthetic depth book also “ticks” when refreshed:

```text
ASK 5982.25 30 → 32
BID 5981.75 60 → 55
MID 5982.00 → 5982.25
```

The browser polls `/api/depth/ES` periodically so the DOM appears alive even when no order is submitted.

## Risk Controls

`RiskEngine` performs pre-trade checks before an order is routed/accepted:

| Check | Behavior |
|---|---|
| Quantity check | Rejects non-positive quantity |
| Side check | Allows only BUY/SELL |
| Max order quantity | Rejects orders above configured quantity limit |
| Max notional | Rejects orders above configured notional limit |
| Fat-finger band | Rejects limit prices more than 5% away from reference price |
| Position limit | Rejects orders that would breach max position |
| Demo venue throttle | Rare low-rate synthetic reject for small demo/market-maker orders |

Example rejects:

```text
Rejected - Position Limit Exceeded
Rejected - Fat Finger Check - limit price outside 5% reference band
Rejected - Risk Limit Breach - simulated venue throttle
```

Rejected orders appear as red lifecycle rows in the UI.

## Position and PnL

`PositionManager` applies fills incrementally:

```text
Fill
    ↓
PositionManager.ApplyFill
    ↓
Position quantity / average price / mark price / unrealized PnL
```

It tracks:

- signed quantity
- average price
- mark price
- derived unrealized PnL
- update time

PnL is currently derived from position state. There is no separate PnL service and no realized PnL/fees model.

## Market Maker Demo

The UI includes an optional market-maker mode. It is intentionally UI-driven rather than a backend hosted strategy service.

When enabled, the browser periodically:

1. fetches `/api/depth/ES`
2. fetches `/api/market-maker/state`
3. submits small bid/ask limit orders if inventory risk allows
4. occasionally submits a tiny market order to demonstrate inventory/PnL movement

Market-maker risk state is exposed by:

```text
GET /api/market-maker/state
```

The state includes:

```text
Inventory
InventoryLimit
Status
BidEnabled
AskEnabled
```

Example states:

```text
Inventory:       +27
Inventory Limit: 50
Status:          NORMAL
Quotes:          BID/ASK
```

```text
Inventory:       +43
Inventory Limit: 50
Status:          INVENTORY LONG
Quotes:          —/ASK
```

```text
Inventory:       +50
Inventory Limit: 50
Status:          BID DISABLED
Quotes:          —/ASK
```

This demonstrates the basic market-making idea of inventory-aware quoting without implementing a full strategy engine.

## Latency Breakdown

Each submitted order returns a simulated latency breakdown:

```text
Risk Check     3.1ms
Route          5.4ms
Exchange      12.2ms
Fill          19.5ms
Total         40.2ms
```

These are demo path timings measured around the simulated stages and small `Task.Delay` hops. They are useful for explaining order-path mechanics, not for claiming production exchange latency.

## API Surface

### Market Data / Metrics

| Endpoint | Description |
|---|---|
| `GET /api/health` | Basic health check |
| `GET /api/metrics` | Performance snapshot |
| `GET /api/metrics/stream` | SSE metrics stream |
| `GET /api/queue/stats` | Queue/coalescing/lossless stats |
| `GET /api/ticks/recent` | Recent in-memory tick log |
| `GET /api/ticks/from/{sequenceId}` | Recent ticks from sequence |
| `GET /api/ticks/stats` | Lossless tick store stats |
| `GET /api/system/metrics` | CPU/memory/thread metrics |

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
| `POST /api/orders` | Submit an order |
| `GET /api/orders` | All known orders |
| `GET /api/orders/open` | Working/open orders |
| `DELETE /api/orders/{orderId}` | Cancel an open order |
| `PUT /api/orders/{orderId}` | Modify an open order |
| `GET /api/executions` | Execution reports |
| `GET /api/execution-stats` | Order/fill/cancel/PnL statistics |
| `GET /api/depth/{symbol}` | Current synthetic DOM snapshot |
| `GET /api/positions` | Current positions |
| `GET /api/market-maker/state` | Inventory-risk state for market-maker demo |

## Concurrency Model

| Component | Model | Notes |
|---|---|---|
| MarketDataSimulator | `BackgroundService` | Generates synthetic ticks continuously |
| LosslessTickStore | `BackgroundService` + bounded channel | Stores recent accepted ticks and journal batches |
| TradeQueueProcessor | `BackgroundService` + bounded channel | Coalesces latest symbol snapshots for SignalR |
| ReplayEngine | async tasks | Runs one replay at a time via cancellation token |
| ExchangeSimulator | singleton + locks | Protects in-memory orders, executions, lifecycle, and synthetic DOM |
| PositionManager | singleton + locks | Protects position dictionary |
| SignalR hub | event-driven | Streams market data/stats to browser clients |

The market-data path uses channels. The order path currently uses direct REST calls into singleton services, not an order `Channel<T>`.

## Deployment Topology

The app is designed to run locally or as a containerized ASP.NET Core service on Azure Container Apps.

```text
Azure Container Apps
├── ASP.NET Core API
├── Static browser terminal
├── SignalR hub
├── background market-data services
└── optional journal adapters

Optional Azure integrations
├── Blob Storage journal
├── Event Hubs journal adapter
├── Container Registry
└── Log Analytics
```

## What This Demo Demonstrates Well

- high-throughput synthetic market-data generation
- bounded-channel backpressure and coalesced UI snapshots
- SignalR live market-data streaming
- sequence IDs and replay-oriented tick storage
- deterministic replay scenarios
- REST order submission
- pre-trade risk checks
- stateful synthetic DOM liquidity consumption
- partial fills and average fill price
- order lifecycle and latency breakdown
- open orders, cancel, and modify behavior
- position updates and derived unrealized PnL
- market-maker inventory-risk display

## What This Demo Intentionally Does Not Implement

- real exchange connectivity
- real matching engine
- real price-time-priority order book
- order queue position
- FIX protocol
- separate OMS/EMS services
- separate PnL service
- Kafka/EventStore event bus
- persistent order/execution database
- multi-account permissions
- production-grade risk/compliance controls

These boundaries are intentional so the project remains a focused portfolio demo rather than an incomplete production trading system.
