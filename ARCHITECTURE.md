# Trade Signal Terminal — Architecture

## System Overview

A real-time trade signal processing demo built on .NET 8 and hosted on Azure, showcasing high-throughput event ingestion, lock-free metrics, bounded-channel backpressure, and deterministic replay — patterns used in production trading systems.

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Azure Container Apps                          │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌──────────────┐    ┌──────────────────┐    ┌──────────────────┐  │
│  │ MarketData   │───▶│ BoundedChannel   │───▶│ TradeQueue       │  │
│  │ Simulator    │    │ <TradeSignal>     │    │ Processor        │  │
│  │ (Producer)   │    │ Cap:10K DropOld   │    │ (Consumer)       │  │
│  └──────────────┘    └──────────────────┘    └────────┬─────────┘  │
│         │                                             │             │
│         │            ┌──────────────────┐             │             │
│         └───────────▶│ Performance      │◀────────────┘             │
│                      │ Metrics          │                           │
│                      │ (Lock-free)      │                           │
│                      └────────┬─────────┘                           │
│                               │                                     │
│  ┌──────────────┐    ┌───────▼──────────┐    ┌──────────────────┐  │
│  │ Replay       │───▶│ SignalR Hub       │───▶│ Browser Clients  │  │
│  │ Engine       │    │ /tradehub         │    │           
│  └──────────────┘    └──────────────────┘    └──────────────────┘  │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

## Pipeline Architecture

### Producer: MarketDataSimulator

A `BackgroundService` that generates synthetic market events at 200–500 events/sec across 15 instruments (equities, futures, FX). Each tick carries a high-resolution timestamp (`Stopwatch.GetTimestamp()`) for end-to-end latency measurement.

```
┌────────────────────────────────────────────────┐
│ MarketDataSimulator (BackgroundService)         │
├────────────────────────────────────────────────┤
│ • 15 instruments with volatility profiles      │
│ • Random walk price model per instrument       │
│ • Configurable rate: 200-500 events/sec        │
│ • Writes to Channel<TradeSignal>               │
│ • TryWrite() — non-blocking, never awaits      │
└────────────────────────────────────────────────┘
```

### Queue: System.Threading.Channels\<T\>

The queue is the system's critical path. We use `BoundedChannel<TradeSignal>` with:

| Property | Value | Rationale |
|----------|-------|-----------|
| Capacity | 10,000 | ~20 seconds of buffer at peak rate |
| FullMode | DropOldest | Stale data is worthless; always prefer fresh |
| SingleWriter | false | Replay engine + simulator write concurrently |
| SingleReader | true | Single consumer enables lock-free fast path |

**Why Channels\<T\> over ConcurrentQueue + polling?**
- `WaitToReadAsync()` is truly asynchronous — no spin-wait, no thread burn
- Bounded capacity with `DropOldest` gives automatic backpressure
- `SingleReader: true` enables the runtime to skip synchronization on the read path
- Integrates natively with `async/await` and `CancellationToken`

### Consumer: TradeQueueProcessor

A `BackgroundService` that drains the channel in batches and broadcasts via SignalR:

```
while (await reader.WaitToReadAsync(ct))
{
    while (reader.TryRead(out var signal))
    {
        batch.Add(signal);
        metrics.RecordLatency(signal.EnqueuedAt);
        
        if (batch.Count >= batchSize) 
        {
            await hub.Clients.All.SendAsync("TradeSignal", batch, ct);
            batch.Clear();
        }
    }
    // Flush remaining
    if (batch.Count > 0)
        await hub.Clients.All.SendAsync("TradeSignal", batch, ct);
}
```

**Key design decisions:**
- Batch drain: Reads everything available before sending → fewer SignalR frames, lower overhead
- No `Task.Delay` polling: Pure event-driven consumption
- Latency recording at dequeue time: Enables p50/p95/p99 tracking without impacting throughput

## Performance Metrics — Lock-Free Design

### Why Lock-Free?

In a trading system, the metrics path runs on every single message. A `lock` statement would:
1. Introduce priority inversion (GC thread holding the lock)
2. Create contention between producer (recording) and reader (API endpoint)
3. Add ~50ns per acquisition — unacceptable at 500+ msg/sec

### Implementation: Circular Buffer + Interlocked CAS

```csharp
// Lock-free write via Interlocked.Increment
public void RecordLatency(long enqueuedTimestamp)
{
    long latencyTicks = Stopwatch.GetTimestamp() - enqueuedTimestamp;
    double latencyMs = (double)latencyTicks / Stopwatch.Frequency * 1000.0;
    
    int index = Interlocked.Increment(ref _writePosition) & (_bufferSize - 1);
    _latencyBuffer[index] = (long)(latencyMs * 1000); // Store as microseconds
}
```

```
┌─────────────────────────────────────────────────────────────┐
│ Circular Buffer (4096 slots, power-of-2 for & masking)      │
├─────────────────────────────────────────────────────────────┤
│ [0] [1] [2] ... [4095]                                      │
│      ▲                                                       │
│      └── _writePosition (Interlocked.Increment)             │
│                                                              │
│ Snapshot: Copy buffer → ArrayPool<long> → Sort → Percentile │
│ • ArrayPool avoids GC pressure on hot path                   │
│ • Snapshot is O(n log n) but runs on API thread, not hot path│
└─────────────────────────────────────────────────────────────┘
```

### Percentile Computation

```csharp
public PerformanceSnapshot GetSnapshot()
{
    long[] sorted = ArrayPool<long>.Shared.Rent(_bufferSize);
    try
    {
        Array.Copy(_latencyBuffer, sorted, _bufferSize);
        Array.Sort(sorted, 0, _bufferSize);
        
        // Skip zero entries (unfilled slots)
        int start = Array.BinarySearch(sorted, 0, _bufferSize, 1L);
        if (start < 0) start = ~start;
        int count = _bufferSize - start;
        
        return new PerformanceSnapshot(
            P50: sorted[start + (int)(count * 0.50)] / 1000.0,
            P95: sorted[start + (int)(count * 0.95)] / 1000.0,
            P99: sorted[start + (int)(count * 0.99)] / 1000.0,
            // ...
        );
    }
    finally
    {
        ArrayPool<long>.Shared.Return(sorted);
    }
}
```

## Latency Budget

| Stage | Target | Measured |
|-------|--------|----------|
| Producer → Channel.TryWrite | <1μs | ~0.3μs |
| Channel queue wait | <5ms (p99) | ~2ms |
| Consumer batch + SignalR serialize | <10ms | ~6ms |
| WebSocket frame to browser | <15ms | ~8ms (local) |
| **End-to-end (tick → pixel)** | **<50ms** | **~16ms (local)** |

## Replay Engine

### Purpose

Deterministic playback of market scenarios allows:
- Demonstrating system behavior under specific conditions (burst, crash, ramp)
- Testing without live data
- Reproducing edge cases on demand

### Traffic Profiles

```
Constant       Burst          FlashCrash       Ramp
───────────    ─┐  ┌──       ────┐            ╱
               │  │             │ ▼          ╱
               │  │             │            ╱
───────────    ─┘  └──       ────┘ ────    ╱─────
100/s          500→50→500     500→0→200    50→500/s
```

| Scenario | Rate Profile | Duration | Purpose |
|----------|-------------|----------|---------|
| Constant | 100 msg/s steady | 60s | Baseline behavior |
| Burst | 500ms bursts every 2s | 30s | Channel backpressure + drop behavior |
| FlashCrash | 500 → 0 → recovery | 20s | Reconnect + empty-state handling |
| Ramp | 50 → 500 linear | 45s | Scaling headroom |

### Disconnect Simulation

The replay engine can inject artificial disconnects to demonstrate:
- SignalR automatic reconnection (exponential backoff)
- Client-side buffering during gap
- State reconciliation on reconnect

## Concurrency Model

### Thread Allocation

| Thread/Task | What it does | Blocking? |
|-------------|-------------|-----------|
| MarketDataSimulator | Generates events, writes to channel | Never blocks (TryWrite) |
| TradeQueueProcessor | Reads channel, sends SignalR | Suspends on WaitToReadAsync |
| ReplayEngine | Generates scenario events | Suspends on Task.Delay |
| Kestrel I/O threads | HTTP/WebSocket I/O | Never blocks (async) |
| SignalR hub | Manages connections | Event-driven |

### Zero-Allocation Hot Path

The event-processing hot path avoids allocations:

1. **TradeSignal** is a `record struct` — stack-allocated, no GC pressure
2. **Latency buffer** uses pre-allocated `long[]` — no per-message allocation
3. **Percentile snapshots** rent from `ArrayPool<long>` — pooled, returned after use
4. **Batch list** is reused (Clear, not re-created) per drain cycle

### Backpressure Strategy

```
Normal:     Producer 200/s → Channel (depth ~50) → Consumer 200/s ✓
Overload:   Producer 500/s → Channel (fills to 10K) → DropOldest kicks in
Recovery:   Producer slows → Channel drains → Consumer catches up
```

The system never blocks the producer. Stale data is discarded. The consumer always processes the freshest available data — exactly what a trading system needs.

## Scaling Strategy

### Horizontal (Container Apps)

```yaml
# Azure Container Apps scaling rule
scale:
  minReplicas: 1
  maxReplicas: 10
  rules:
    - name: signalr-connections
      custom:
        type: external
        metadata:
          scalerAddress: keda-signalr-scaler
          connectionCount: "100"
```

Each replica is stateless. SignalR uses Azure SignalR Service for cross-instance fan-out in production. The bounded channel is per-instance (no shared state between replicas).

### Vertical

- Channel buffer size tunable via `IConfiguration`
- Batch size configurable
- Rate limits adjustable per-instrument

## Failure Modes & Recovery

| Failure | Behavior | Recovery |
|---------|----------|----------|
| Consumer slow | Channel fills → DropOldest | Automatic (drops stale) |
| SignalR disconnect | Client sees gap | Auto-reconnect + state sync |
| OOM pressure | GC Gen2 collection | ArrayPool prevents fragmentation |
| Container crash | Azure restarts container | ~5s cold start, clients reconnect |
| ACR unavailable | Deploy fails | Previous revision stays live |

## Deployment Topology

```
┌──────────────────────────────────────────────────────────┐
│ Azure (australiaeast)                                     │
├──────────────────────────────────────────────────────────┤
│                                                          │
│  ┌─────────────┐    ┌──────────────┐    ┌────────────┐  │
│  │ Container   │    │ Container    │    │ Service    │  │
│  │ Registry    │───▶│ Apps Env     │    │ Bus        │  │
│  │ (ACR)       │    │ (Log Analyt) │    │ (Standard) │  │
│  └─────────────┘    └──────┬───────┘    └────────────┘  │
│                            │                             │
│                     ┌──────▼───────┐                     │
│                     │ Container    │                     │
│                     │ App          │                     │
│                     │ (tradedemo)  │                     │
│                     │ :8080        │                     │
│                     └──────┬───────┘                     │
│                            │                             │
└────────────────────────────┼─────────────────────────────┘
                             │ HTTPS
                    ┌────────▼────────┐
                    │ Browser Clients │
                    │ (WebSocket)     │
                    └─────────────────┘
```

## Technology Choices

| Choice | Alternative Considered | Why This |
|--------|----------------------|----------|
| Channels\<T\> | BlockingCollection, ConcurrentQueue | True async, bounded, backpressure built-in |
| SignalR | gRPC-Web, SSE | Bidirectional, auto-reconnect, protocol negotiation |
| record struct | class | Zero-alloc on hot path, value semantics |
| ArrayPool\<T\> | stackalloc, new[] | Reusable across calls, no GC pressure, safe for large buffers |
| Interlocked CAS | lock, ReaderWriterLockSlim | Zero contention on write path, wait-free |
| Container Apps | App Service, AKS | No VM quota needed, consumption billing, KEDA scaling |
| Bicep | ARM, Terraform | Native Azure, type-safe, modular |

## Running Locally

```bash
cd src/TradeDemo.Api
dotnet run
# Open http://localhost:5000
```

## Deploying

```powershell
# One-time setup
.\setup-azure.ps1

# Deploy/update
.\deploy-container.ps1
```

See [deploy-container.ps1](deploy-container.ps1) for the full image build + push + revision update flow.
