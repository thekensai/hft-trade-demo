using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using TradeDemo.Wpf.Models;

namespace TradeDemo.Wpf.Services;

public sealed class TradeApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public TradeApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public Task<QueueStatsDto?> GetQueueStatsAsync(string baseUrl, CancellationToken cancellationToken = default) =>
        GetAsync<QueueStatsDto>(baseUrl, "/api/queue/stats", cancellationToken);

    public Task<SystemMetricsDto?> GetSystemMetricsAsync(string baseUrl, CancellationToken cancellationToken = default) =>
        GetAsync<SystemMetricsDto>(baseUrl, "/api/system/metrics", cancellationToken);

    public Task<DepthSnapshotDto?> GetDepthAsync(string baseUrl, string symbol, CancellationToken cancellationToken = default) =>
        GetAsync<DepthSnapshotDto>(baseUrl, $"/api/depth/{Uri.EscapeDataString(symbol)}", cancellationToken);

    public Task<List<PositionDto>?> GetPositionsAsync(string baseUrl, CancellationToken cancellationToken = default) =>
        GetAsync<List<PositionDto>>(baseUrl, "/api/positions", cancellationToken);

    public Task<List<TradeMonitorRowDto>?> GetTradeMonitorAsync(string baseUrl, CancellationToken cancellationToken = default) =>
        GetAsync<List<TradeMonitorRowDto>>(baseUrl, "/api/trade-monitor", cancellationToken);

    public Task<ExecutionStatsDto?> GetExecutionStatsAsync(string baseUrl, CancellationToken cancellationToken = default) =>
        GetAsync<ExecutionStatsDto>(baseUrl, "/api/execution-stats", cancellationToken);

    public Task<MarketMakerStateDto?> GetMarketMakerStateAsync(string baseUrl, CancellationToken cancellationToken = default) =>
        GetAsync<MarketMakerStateDto>(baseUrl, "/api/market-maker/state", cancellationToken);

    public Task<List<OrderLifecycleEventDto>?> GetLifecycleAsync(string baseUrl, int count = 80, CancellationToken cancellationToken = default) =>
        GetAsync<List<OrderLifecycleEventDto>>(baseUrl, $"/api/lifecycle/recent?count={count}", cancellationToken);

    public async Task<OrderResultDto?> SubmitBuyOrderAsync(string baseUrl, string symbol, int quantity = 100, CancellationToken cancellationToken = default)
    {
        var order = new OrderDto
        {
            Symbol = symbol,
            Side = "BUY",
            Quantity = quantity,
            RemainingQuantity = quantity,
            OrderType = "Market",
            Owner = "WPF"
        };

        using var response = await _httpClient.PostAsJsonAsync(BuildUri(baseUrl, "/api/orders"), order, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OrderResultDto>(JsonOptions, cancellationToken);
    }

    public async Task ResetDemoAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(BuildUri(baseUrl, "/api/demo/reset"), null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<T?> GetAsync<T>(string baseUrl, string path, CancellationToken cancellationToken)
    {
        return await _httpClient.GetFromJsonAsync<T>(BuildUri(baseUrl, path), JsonOptions, cancellationToken);
    }

    private static string BuildUri(string baseUrl, string path) => $"{baseUrl.TrimEnd('/')}{path}";
}
