# TradeDemo — Real-Time Trading Infrastructure Demo

TradeDemo is a .NET 8 portfolio/demo application for high-throughput market-data streaming and order/execution simulation. It includes:

- an ASP.NET Core API host with static browser UI, SignalR, REST APIs, and background services
- a browser trading terminal built with static HTML/CSS/vanilla JavaScript
- a WPF desktop terminal that connects to the same backend
- synthetic market-data generation with bounded-channel backpressure
- a FIFO order command queue, risk checks, synthetic depth-of-market fills, lifecycle events, and PnL
- replay support from generated scenarios, recent in-memory ticks, and optional tick journals

This is not a production trading system or real exchange gateway. It is a focused demo of trading-system mechanics: fan-out, coalescing, replay, queueing, risk checks, partial fills, inventory risk, and metrics.

## Solution Layout

```text
TradeDemo.sln
├── src/TradeDemo.Api
│   ├── Program.cs                     # Minimal APIs, SignalR, DI, static files
│   ├── Hubs/TradeHub.cs               # SignalR hub
│   ├── Services/                      # market data, replay, order queue, exchange, risk, positions
│   ├── Journal/                       # null/local/blob/event-hubs tick journal adapters
│   └── wwwroot/                       # browser terminal + explainer pages
│
├── src/TradeDemo.Wpf
│   ├── MainWindow.xaml                # desktop terminal shell
│   ├── ViewModels/                    # MVVM state and commands
│   └── Services/                      # REST + SignalR clients, client-side feed processor
│
└── src/TradeDemo.Tests                # test project
```

For the deeper architecture document, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Current Architecture

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

Important implementation boundaries:

- Market data uses in-process fan-out via `IMarketDataBus`; it is not Kafka/EventStore.
- Order mutations use an in-process `Channel<QueuedOrderCommand>`; it is not Azure Service Bus.
- Orders, executions, depth, positions, and lifecycle events are in-memory only.
- Tick journaling is optional and disabled by default.
- The exchange simulator demonstrates fills and order lifecycle; it is not a real matching engine.

## Run Locally

### Browser/API host

```bash
cd src/TradeDemo.Api
dotnet run
```

Launch settings expose both:

- <https://localhost:5001>
- <http://localhost:5000>

### WPF terminal

Start the API host first, then run the desktop project:

```bash
dotnet run --project src/TradeDemo.Wpf
```

The WPF terminal defaults to the hosted backend URL in its view model, but the UI exposes the backend URL so it can point at a local API instance.

### Build everything

```bash
dotnet build TradeDemo.sln
```

## Features

| Feature | Description |
|---|---|
| Browser trading terminal | Live price grid, signal feed, ticker tape, depth, lifecycle, positions, market-maker state, metrics |
| WPF trading terminal | Desktop client using the same REST APIs and SignalR hub |
| High-throughput market data | `MarketDataSimulator` generates synthetic ticks across equities, futures-style symbols, and crypto |
| In-process market-data bus | `InMemoryMarketDataBus` fans each tick to independent subscribers |
| Coalesced SignalR stream | `TradeQueueProcessor` drains raw ticks and broadcasts latest-by-symbol snapshots |
| Recent tick storage | `LosslessTickStore` keeps a 500K tick ring buffer for replay-oriented workflows |
| Optional tick journal | Null, local segment file, Azure Blob, and Azure Event Hubs adapters behind `ITickJournal` |
| Replay engine | Scenario replay, recent tick replay, and journal replay |
| FIFO order command queue | Submit/cancel/modify operations serialize through `OrderCommandQueue` and `OrderCommandProcessor` |
| Risk engine | Quantity, side, max quantity, notional, fat-finger, position-limit, and demo throttle checks |
| Exchange simulator | Stateful synthetic DOM, market/limit behavior, partial fills, open orders, cancel, modify |
| Position and PnL | `PositionManager` tracks quantity, average price, realized PnL, mark price, and unrealized PnL |
| Metrics | Queue depth, processed/dropped/coalesced counts, latency percentiles, GC, CPU, memory, thread count |

## Key Runtime Settings

| Component | Current behavior |
|---|---|
| `TradeQueueProcessor` | `Channel<TradeSignal>` capacity `100_000`, `DropOldest`, single reader, multiple writers, ~33ms snapshot cadence |
| `LosslessTickStore` | `Channel<TradeSignal>` capacity `1_000_000`, recent ring capacity `500_000`, optional journal batching |
| `OrderCommandQueue` | `Channel<QueuedOrderCommand>` capacity `4_096`, `FullMode=Wait`, single-reader FIFO command loop |
| `PerformanceMetrics` | fixed `long[4096]` latency sample buffer, `Interlocked` counters, `ArrayPool<long>` percentile sorting |
| GC latency | `GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency` in `Program.cs` |

## Site Pages

| Page | URL | What it shows |
|---|---|---|
| Live Terminal | `/` | Real-time trading UI with market data, orders, depth, lifecycle, positions, and metrics |
| Replay Engine | `/replay.html` | Scenario playback and replay controls |
| Performance | `/performance.html` | Latency, throughput, GC, allocation, CPU, memory, and queue metrics |
| Architecture | `/architecture.html` | Current system architecture and API topology |
| Concurrency | `/concurrency.html` | Actual channel, background-service, locking, and client concurrency model |

## API Surface

### Market Data / Metrics

| Method | Endpoint | Description |
|---|---|---|
| GET | `/api/health` | Basic health check |
| GET | `/api/metrics` | Performance snapshot |
| GET | `/api/metrics/stream` | SSE metrics stream |
| GET | `/api/queue/stats` | Market-data queue/coalescing/lossless stats |
| GET | `/api/orders/queue/stats` | Order-command queue stats |
| GET | `/api/concurrency/threads` | Demo thread/rate breakdown for concurrency page |
| GET | `/api/ticks/recent` | Recent in-memory tick log |
| GET | `/api/ticks/from/{sequenceId}` | Recent ticks from sequence |
| GET | `/api/ticks/stats` | Lossless tick store stats |
| GET | `/api/system/metrics` | CPU/memory/thread metrics snapshot |

### Replay

| Method | Endpoint | Description |
|---|---|---|
| GET | `/api/replay/scenarios` | Available replay scenarios |
| POST | `/api/replay/start` | Start a scenario replay with a `ReplayScenario` JSON body |
| POST | `/api/replay/recent?count=10000&speed=1` | Replay recent in-memory ticks |
| POST | `/api/replay/journal/from/{sequenceId}?count=10000&speed=1` | Replay journal ticks from a sequence ID |
| POST | `/api/replay/stop` | Stop replay |
| GET | `/api/replay/state` | Current replay state |

### Order / Execution

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/orders` | Submit an order through the command queue |
| GET | `/api/orders` | All known orders |
| GET | `/api/orders/open` | Working/open orders |
| DELETE | `/api/orders/{orderId}` | Cancel an open order through the command queue |
| PUT | `/api/orders/{orderId}` | Modify an open order through the command queue |
| GET | `/api/executions` | Execution reports |
| GET | `/api/lifecycle/recent` | Recent order lifecycle events |
| GET | `/api/execution-stats` | Order/fill/cancel/PnL statistics |
| GET | `/api/trade-monitor` | Trade-monitor rows for terminal UI |
| GET | `/api/depth/{symbol}` | Current synthetic depth-of-market snapshot |
| GET | `/api/positions` | Current positions |
| GET | `/api/market-maker/state` | Inventory-risk state for market-maker demo |
| POST | `/api/demo/reset` | Reset in-memory demo trading state |

### SignalR

| Endpoint | Messages |
|---|---|
| `/tradehub` | `TradeSignals`, `Stats`, `ReplayStateChanged` |

## Replay and Tick Journaling

Replay has two modes:

1. **Scenario replay** generates fresh synthetic ticks, appends them to `LosslessTickStore`, and sends them to the UI path.
2. **Historical replay** reads recent or journal ticks and sends them to the UI path only, avoiding duplicate authoritative storage.

Journaling is disabled by default. Enable a local journal with environment variables:

```powershell
$env:TickJournal__Enabled = "true"
$env:TickJournal__Provider = "Local"
$env:TickJournal__Local__DirectoryPath = "data/tick-journal"
dotnet run --project src/TradeDemo.Api
```

Journal files are written relative to the API process working directory. When running from `src/TradeDemo.Api`, the default local path is:

```text
src/TradeDemo.Api/data/tick-journal/
```

Blob and Event Hubs journal adapters are implemented behind the same interfaces, but deployment credentials/configuration must be supplied before using them outside local development.

## Azure Deployment

The repository includes Azure Container Apps infrastructure scripts under [infra/](infra/).

### 1. Bootstrap Azure prerequisites

```powershell
.\infra\setup-azure.ps1
```

### 2. Build image, deploy infrastructure, and deploy container

Use this for first deployment or when infrastructure changes:

```powershell
.\infra\deploy-container.ps1 -DeployInfra
```

### 3. Deploy app-only changes

Use this after infrastructure already exists:

```powershell
.\infra\deploy-container.ps1
```

The Bicep template provisions:

- Azure Container Apps environment
- Container App for the ASP.NET Core API/static site
- Log Analytics workspace
- Azure Service Bus namespace and `market-events` queue

The current application runtime uses in-process channels for the demo paths. Service Bus is provisioned as infrastructure scaffolding for a production-style ingestion extension, not as the active in-process market-data or order-command queue.

## Custom Domain Helper

Use [infra/bind-custom-domain.ps1](infra/bind-custom-domain.ps1) to print required DNS records and bind a hostname after propagation.

Subdomain example:

```powershell
.\infra\bind-custom-domain.ps1 -Hostname trade.example.com
.\infra\bind-custom-domain.ps1 -Hostname trade.example.com -Bind
```

Apex/root domain example:

```powershell
.\infra\bind-custom-domain.ps1 -Hostname example.com -ApexDomain
.\infra\bind-custom-domain.ps1 -Hostname example.com -ApexDomain -Bind
```

Subdomains are simpler than apex/root domains and are recommended for this project.

## Intentional Non-Goals

TradeDemo intentionally does not implement:

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
