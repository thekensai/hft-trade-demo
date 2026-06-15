# TradeDemo — Real-Time Trading Signal Terminal

A web-based demo of a high-throughput trading signal UI backed by queue-processed market data simulation, deployed on Azure.

Run locally with dotnet run from src/TradeDemo.Api and open https://localhost:5001. 

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  Azure App Service (Web App)                                │
│  ┌───────────────────────────────────────────────────────┐  │
│  │  ASP.NET Core API                                     │  │
│  │  ┌─────────────────┐    ┌──────────────────────────┐  │  │
│  │  │ MarketData      │───▶│ Bounded Channel (Queue)  │  │  │
│  │  │ Simulator       │    │ 10K capacity, backpressure│  │  │
│  │  │ (15 instruments)│    └──────────┬───────────────┘  │  │
│  │  └─────────────────┘               │                  │  │
│  │                          ┌─────────▼───────────┐      │  │
│  │                          │ TradeQueueProcessor │      │  │
│  │                          │ (batch consume)     │      │  │
│  │                          └─────────┬───────────┘      │  │
│  │                                    │ SignalR           │  │
│  │  HTTP Order API ─▶ OrderCommandQueue ─▶ Processor ─▶ Exchange/Risk/Position │
│  │  ┌────────────────────────────────▼──────────────┐   │  │
│  │  │ Static Files (Trading Terminal UI)             │   │  │
│  │  │ - Fast-scroll signal feed                      │   │  │
│  │  │ - Live price grid with flash effects           │   │  │
│  │  │ - Order flow heatmap                           │   │  │
│  │  │ - Ticker tape                                  │   │  │
│  │  └───────────────────────────────────────────────┘   │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
        │
        │ (Optional: Azure Service Bus for production)
        ▼
┌─────────────────────────────┐
│ Azure Service Bus Queue     │
│ "market-events"             │
│ - Partitioned               │
│ - 5min TTL, batch enabled   │
└─────────────────────────────┘
```

## Run Locally

```bash
cd src/TradeDemo.Api
dotnet run
# Open https://localhost:5001
```

## Deploy to Azure

Uses **Azure Container Apps** (consumption-based, no App Service VM quota needed).

### 1. Bootstrap Azure prerequisites

```bash
.\infra\setup-azure.ps1
```

### 2. Build, Deploy Infrastructure & Deploy Container

Run this when infrastructure changes, or for the first deployment. The script builds the container image first, then passes that image into `infra/main.bicep` so the required `containerImage` parameter is always explicit.

```bash
.\infra\deploy-container.ps1 -DeployInfra
```

### 3. Deploy App-Only Changes

For code/frontend-only changes after infrastructure already exists:

```bash
.\infra\deploy-container.ps1
```

Or run with explicit settings:
```bash
.\infra\setup-azure.ps1 -ResourceGroupName rg-tradedemo -Location australiaeast
.\infra\deploy-container.ps1 -ResourceGroupName rg-tradedemo -Location australiaeast -DeployInfra
```

## Features

| Feature | Description |
|---------|-------------|
| **Fast-scroll signal feed** | 60-row bounded feed with BUY/SELL color coding, sub-100ms animation |
| **Live price grid** | 15 instruments with green/red flash on tick |
| **Order flow heatmap** | Buy vs sell volume ratio per symbol |
| **Ticker tape** | Continuous horizontal scroll of latest prices |
| **Queue simulation** | Bounded Channel (10K) with backpressure + drop-oldest semantics |
| **Order command queue** | HTTP order commands serialize through a bounded FIFO Channel before exchange/risk/position processing |
| **Throughput stats** | Real-time msg/sec, queue depth, processed/dropped counters |
| **High-frequency generation** | 5-20 events per 20-80ms tick (~200-500 events/sec) |
| **Lock-free latency metrics** | p50/p95/p99 percentiles via Interlocked CAS + ArrayPool |
| **Replay engine** | Deterministic playback with Constant/Burst/FlashCrash/Ramp profiles |
| **Multi-page engineering showcase** | Architecture, performance, concurrency, and replay explanation pages |

## Site Pages

The deployed site has multiple pages, each targeting a specific engineering concern:

| Page | URL | What it shows |
|------|-----|---------------|
| **Live Terminal** | `/` | Real-time trading UI with signal feed, price grid, order flow |
| **Architecture** | `/architecture.html` | System design, pipeline flow, latency budget, scaling strategy |
| **Performance** | `/performance.html` | Live p50/p95/p99 latency bars, GC metrics, allocation rate |
| **Concurrency** | `/concurrency.html` | Threading model, Channels\<T\> patterns, lock-free code samples |
| **Replay Engine** | `/replay.html` | Configurable scenario playback, traffic profile visualization |

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/metrics` | GET | Current performance snapshot (latencies, GC, allocations) |
| `/api/metrics/stream` | GET | Server-Sent Events stream of metrics (1/sec) |
| `/api/queue/stats` | GET | Market-data queue depth, processed count, dropped count |
| `/api/orders/queue/stats` | GET | Order command queue depth, processed count, failures, queue wait, processing time |
| `/api/replay/scenarios` | GET | Available replay scenarios |
| `/api/replay/start/{id}` | POST | Start a replay scenario |
| `/api/replay/stop` | POST | Stop current replay |
| `/api/replay/state` | GET | Current replay engine state |
| `/tradehub` | WebSocket | SignalR hub for real-time trade signals |

## Azure Resources

- **Container Apps Environment** (Consumption): Hosts .NET 8 container, WebSockets supported, auto-scale 1-3 replicas
- **Log Analytics Workspace**: Container app logs and monitoring
- **Service Bus** (Standard): Queue `market-events` for production event ingestion

## Short URL

Use a custom domain to get a short URL like `trade.example.com` instead of the Azure-managed hostname.

Subdomain flow:

```powershell
.\bind-custom-domain.ps1 -Hostname trade.example.com
```

That prints the exact DNS records you need to create:

- `CNAME trade.example.com -> <your container app fqdn>`
- `TXT asuid.trade.example.com -> <your verification id>`

After those records propagate, bind the hostname:

```powershell
.\bind-custom-domain.ps1 -Hostname trade.example.com -Bind
```

Root domain flow:

```powershell
.\bind-custom-domain.ps1 -Hostname example.com -ApexDomain
```

That prints the `A` and `TXT` records needed for the apex domain. After propagation:

```powershell
.\bind-custom-domain.ps1 -Hostname example.com -ApexDomain -Bind
```

Subdomains are simpler than apex/root domains and are the recommended option for this project.

## Notes

- Uses Azure Container Apps instead of App Service to avoid App Service VM quota restrictions.
- `setup-azure.ps1` registers the required providers: `Microsoft.App`, `Microsoft.OperationalInsights`, `Microsoft.ContainerRegistry`, and `Microsoft.ServiceBus`.
- `deploy-container.ps1` uses `.NET PublishContainer` and avoids the `az containerapp up` ACR build path, which hit an Azure CLI bug and ACR Tasks restrictions in this subscription.
- `bind-custom-domain.ps1` prints the exact DNS records for a custom hostname and can bind the hostname after propagation.
- Container Apps consumption plan has no upfront VM quota requirement.


Added journal replay support:


POST /api/replay/journal/from/{sequenceId}?count=10000&speed=1
This reads from ITickJournalReader and replays to the UI path only.

How to enable local journal
Run with env vars:


$env:TickJournal__Enabled="true"
$env:TickJournal__Provider="Local"
$env:TickJournal__Local__DirectoryPath="data/tick-journal"
dotnet run
Then journal files should appear under:


src/TradeDemo.Api/data/tick-journal/
Important note
Blob and Event Hubs adapters are implemented behind the same interfaces, but Azure infra/config still needs to be provided before using them in deployment.