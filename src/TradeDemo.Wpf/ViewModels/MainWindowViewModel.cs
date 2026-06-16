using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using TradeDemo.Wpf.Commands;
using TradeDemo.Wpf.Models;
using TradeDemo.Wpf.Services;

namespace TradeDemo.Wpf.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private const int MaxFeedRows = 220;
    private const int MaxFeedRowsPerFrame = 5;
    private const int MaxLifecycleRows = 80;
    private const int MaxTradeMonitorRows = 80;

    private readonly TradeApiClient _apiClient;
    private readonly TradeHubClient _hubClient;
    private readonly TerminalFeedProcessor _feedProcessor;
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _msgRateTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, PriceRowViewModel> _priceBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TickerRowViewModel> _tickerBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OrderFlowAccumulator> _orderFlow = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OrderFlowRowViewModel> _orderFlowRowsBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _lifecycleEventIds = [];
    private readonly int[] _rateHistory = new int[60];

    private int _rateHistoryIndex;
    private bool _rateHistoryFilled;
    private int _msgCounter;
    private bool _isUiRefreshPaused;
    private bool _isConnected;
    private bool _isOrderSubmitInFlight;
    private string _clockText = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    private string _connectionStatusText = "DISCONNECTED";
    private Brush _connectionStatusBrush = Brushes.OrangeRed;
    private string _backendBaseUrl = "https://airtrade.net.au";
    private string _selectedSymbol = "ES";
    private PriceRowViewModel? _selectedPriceRow;
    private long _processedCount;
    private long _droppedCount;
    private int _queueDepth;
    private double _serverRate;
    private double _snapshotsPerSec;
    private long _serverGeneratedTotal;
    private double _coalescedPerSec;
    private int _clientMsgPerSec;
    private string _cpuUsageText = "0%";
    private string _memoryUsageText = "0 MB";
    private string _workingSetText = "0 MB";
    private int _threadCount;
    private string _rateAxisLabel0 = "-0s";
    private string _rateAxisLabel1 = "-0s";
    private string _rateAxisLabel2 = "-0s";
    private string _rateAxisLabel3 = "-0s";
    private string _consumedLiquidityText = "Recent consumed: —";
    private string _matchingModelText = "BOOK 0 · FIFO PRICE-TIME";
    private string _averageFillBadgeText = "FILL AVG —";
    private string _depthMidText = "MID —";
    private DepthSnapshotDto? _currentDepth;
    private ExecutionStatsDto? _currentStats;
    private OrderResultDto? _lastOrderResult;

    public MainWindowViewModel(TradeApiClient apiClient, TradeHubClient hubClient, TerminalFeedProcessor feedProcessor)
    {
        _apiClient = apiClient;
        _hubClient = hubClient;
        _feedProcessor = feedProcessor;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        PauseResumeCommand = new RelayCommand(TogglePause);
        SubmitBuyCommand = new AsyncRelayCommand(SubmitBuyAsync, () => CanSubmitBuy);
        ResetDemoCommand = new AsyncRelayCommand(ResetDemoAsync);

        _clockTimer.Tick += (_, _) => ClockText = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        _msgRateTimer.Tick += (_, _) => UpdateMessageRate();
        _statsTimer.Tick += async (_, _) => await RefreshMetricsAsync();
        _renderTimer.Tick += (_, _) => DrainPendingSignals();

        _hubClient.TradeSignalReceived += _feedProcessor.Enqueue;
        _hubClient.TradeSignalsReceived += _feedProcessor.EnqueueRange;
        _hubClient.StatsReceived += ApplyHubStats;
        _hubClient.ConnectionStatusChanged += SetConnectionStatus;
    }

    public ObservableCollection<PriceRowViewModel> PriceRows { get; } = [];
    public ObservableCollection<FeedRowViewModel> FeedRows { get; } = [];
    public ObservableCollection<TickerRowViewModel> TickerItems { get; } = [];
    public ObservableCollection<OrderFlowRowViewModel> OrderFlowRows { get; } = [];
    public ObservableCollection<DepthLevelViewModel> AskDepthRows { get; } = [];
    public ObservableCollection<DepthLevelViewModel> BidDepthRows { get; } = [];
    public ObservableCollection<LifecycleRowViewModel> LifecycleRows { get; } = [];
    public ObservableCollection<TradeMonitorRowViewModel> TradeMonitorRows { get; } = [];
    public ObservableCollection<int> RateSamples { get; } = [];

    public event EventHandler? LifecycleAppended;
    public PositionSummaryViewModel PositionSummary { get; } = new();
    public MarketMakerSummaryViewModel MarketMakerSummary { get; } = new();
    public ExecutionStatsViewModel ExecutionStats { get; } = new();
    public PortfolioStatsViewModel PortfolioStats { get; } = new();

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public RelayCommand PauseResumeCommand { get; }
    public AsyncRelayCommand SubmitBuyCommand { get; }
    public AsyncRelayCommand ResetDemoCommand { get; }

    public string BackendBaseUrl
    {
        get => _backendBaseUrl;
        set => SetProperty(ref _backendBaseUrl, value.TrimEnd('/'));
    }

    public string ClockText { get => _clockText; private set => SetProperty(ref _clockText, value); }
    public string ConnectionStatusText { get => _connectionStatusText; private set => SetProperty(ref _connectionStatusText, value); }
    public Brush ConnectionStatusBrush { get => _connectionStatusBrush; private set => SetProperty(ref _connectionStatusBrush, value); }
    public bool IsConnected { get => _isConnected; private set { if (SetProperty(ref _isConnected, value)) RaiseCommandStates(); } }
    public string PauseResumeButtonText => _isUiRefreshPaused ? "Resume" : "Pause";
    public int FeedCount => FeedRows.Count;
    public string BuyButtonText => $"BUY 100 {SelectedSymbol}";
    public bool CanSubmitBuy => IsConnected && !_isOrderSubmitInFlight && !string.IsNullOrWhiteSpace(SelectedSymbol);
    public bool IsOrderSubmitInFlight => _isOrderSubmitInFlight;

    public string SelectedSymbol
    {
        get => _selectedSymbol;
        private set
        {
            if (!SetProperty(ref _selectedSymbol, value))
            {
                return;
            }

            PositionSummary.Symbol = value;
            OnPropertyChanged(nameof(BuyButtonText));
            OnPropertyChanged(nameof(DepthTitleText));
            RaiseCommandStates();
            _ = RefreshTradingPanelsAsync();
        }
    }

    public PriceRowViewModel? SelectedPriceRow
    {
        get => _selectedPriceRow;
        set
        {
            if (_selectedPriceRow == value)
            {
                return;
            }

            if (_selectedPriceRow is not null)
            {
                _selectedPriceRow.IsSelected = false;
            }

            _selectedPriceRow = value;
            OnPropertyChanged();

            if (value is not null)
            {
                value.IsSelected = true;
                SelectedSymbol = value.Symbol;
            }
        }
    }

    public long ProcessedCount { get => _processedCount; private set => SetProperty(ref _processedCount, value); }
    public long DroppedCount { get => _droppedCount; private set => SetProperty(ref _droppedCount, value); }
    public int QueueDepth { get => _queueDepth; private set => SetProperty(ref _queueDepth, value); }
    public double ServerRate { get => _serverRate; private set => SetProperty(ref _serverRate, value); }
    public double SnapshotsPerSec { get => _snapshotsPerSec; private set => SetProperty(ref _snapshotsPerSec, value); }
    public long ServerGeneratedTotal { get => _serverGeneratedTotal; private set => SetProperty(ref _serverGeneratedTotal, value); }
    public double CoalescedPerSec { get => _coalescedPerSec; private set => SetProperty(ref _coalescedPerSec, value); }
    public int ClientMsgPerSec { get => _clientMsgPerSec; private set => SetProperty(ref _clientMsgPerSec, value); }
    public string CpuUsageText { get => _cpuUsageText; private set => SetProperty(ref _cpuUsageText, value); }
    public string MemoryUsageText { get => _memoryUsageText; private set => SetProperty(ref _memoryUsageText, value); }
    public string WorkingSetText { get => _workingSetText; private set => SetProperty(ref _workingSetText, value); }
    public int ThreadCount { get => _threadCount; private set => SetProperty(ref _threadCount, value); }
    public string RateAxisLabel0 { get => _rateAxisLabel0; private set => SetProperty(ref _rateAxisLabel0, value); }
    public string RateAxisLabel1 { get => _rateAxisLabel1; private set => SetProperty(ref _rateAxisLabel1, value); }
    public string RateAxisLabel2 { get => _rateAxisLabel2; private set => SetProperty(ref _rateAxisLabel2, value); }
    public string RateAxisLabel3 { get => _rateAxisLabel3; private set => SetProperty(ref _rateAxisLabel3, value); }
    public string CurrentRateText => $"{ClientMsgPerSec} msg/s";
    public string DepthTitleText => $"DEPTH OF MARKET — {SelectedSymbol}";
    public string ConsumedLiquidityText { get => _consumedLiquidityText; private set => SetProperty(ref _consumedLiquidityText, value); }
    public string MatchingModelText { get => _matchingModelText; private set => SetProperty(ref _matchingModelText, value); }
    public string AverageFillBadgeText { get => _averageFillBadgeText; private set => SetProperty(ref _averageFillBadgeText, value); }
    public string DepthMidText { get => _depthMidText; private set => SetProperty(ref _depthMidText, value); }

    public async Task StartAsync()
    {
        _clockTimer.Start();
        _msgRateTimer.Start();
        _renderTimer.Start();
        await ConnectAsync();
    }

    public async Task StopAsync()
    {
        _clockTimer.Stop();
        _msgRateTimer.Stop();
        _renderTimer.Stop();
        _statsTimer.Stop();
        await _hubClient.DisconnectAsync();
    }

    private async Task ConnectAsync()
    {
        try
        {
            await _hubClient.DisconnectAsync();
            _feedProcessor.Clear();
            await _hubClient.ConnectAsync(BackendBaseUrl);
            _statsTimer.Start();
            await ResetDemoAsync();
            await RefreshMetricsAsync();
        }
        catch
        {
            _statsTimer.Stop();
            SetConnectionStatus("CONNECT FAILED");
        }
    }

    private async Task DisconnectAsync()
    {
        _statsTimer.Stop();
        await _hubClient.DisconnectAsync();
    }

    private void TogglePause()
    {
        _isUiRefreshPaused = !_isUiRefreshPaused;
        OnPropertyChanged(nameof(PauseResumeButtonText));
    }

    private async Task SubmitBuyAsync()
    {
        try
        {
            _isOrderSubmitInFlight = true;
            RaiseCommandStates();
            _lastOrderResult = await _apiClient.SubmitBuyOrderAsync(BackendBaseUrl, SelectedSymbol);
            ApplyOrderResult(_lastOrderResult);
            await RefreshTradingPanelsAsync();
        }
        catch
        {
            AddLifecycleMessage("Rejected", $"Order submit failed for {SelectedSymbol}");
        }
        finally
        {
            _isOrderSubmitInFlight = false;
            RaiseCommandStates();
        }
    }

    private async Task ResetDemoAsync()
    {
        await _apiClient.ResetDemoAsync(BackendBaseUrl);
        LifecycleRows.Clear();
        TradeMonitorRows.Clear();
        AskDepthRows.Clear();
        BidDepthRows.Clear();
        _lifecycleEventIds.Clear();
        ConsumedLiquidityText = "Recent consumed: —";
        AverageFillBadgeText = "FILL AVG —";
        _lastOrderResult = null;
        await RefreshTradingPanelsAsync();
    }

    private void DrainPendingSignals()
    {
        if (_isUiRefreshPaused || _feedProcessor.IsEmpty)
        {
            return;
        }

        var result = _feedProcessor.Drain(MaxFeedRowsPerFrame);
        _msgCounter += result.DequeuedCount;

        foreach (var signal in result.LatestBySymbol)
        {
            UpdatePriceAndTicker(signal);
        }

        foreach (var signal in result.FeedSignals)
        {
            RecordOrderFlow(signal);
            AppendFeed(signal);
        }

        RefreshOrderFlowRows();
    }

    private void UpdatePriceAndTicker(TradeSignalDto signal)
    {
        if (!_priceBySymbol.TryGetValue(signal.Symbol, out var priceRow))
        {
            priceRow = new PriceRowViewModel(signal.Symbol, signal.Exchange, signal.MidPrice, signal.BidPrice, signal.AskPrice, signal.ChangePercent);
            _priceBySymbol[signal.Symbol] = priceRow;
            PriceRows.Insert(0, priceRow);
            if (SelectedPriceRow is null || signal.Symbol.Equals(SelectedSymbol, StringComparison.OrdinalIgnoreCase))
            {
                SelectedPriceRow = priceRow;
            }
        }
        else
        {
            priceRow.Update(signal.MidPrice, signal.BidPrice, signal.AskPrice, signal.ChangePercent, signal.Exchange);
        }

        if (!_tickerBySymbol.TryGetValue(signal.Symbol, out var tickerRow))
        {
            tickerRow = new TickerRowViewModel(signal.Symbol, signal.MidPrice, signal.ChangePercent);
            _tickerBySymbol[signal.Symbol] = tickerRow;
            TickerItems.Add(tickerRow);
        }
        else
        {
            tickerRow.Update(signal.MidPrice, signal.ChangePercent);
        }
    }

    private void AppendFeed(TradeSignalDto signal)
    {
        FeedRows.Insert(0, new FeedRowViewModel(signal));
        while (FeedRows.Count > MaxFeedRows)
        {
            FeedRows.RemoveAt(FeedRows.Count - 1);
        }

        OnPropertyChanged(nameof(FeedCount));
    }

    private void RecordOrderFlow(TradeSignalDto signal)
    {
        if (!_orderFlow.TryGetValue(signal.Symbol, out var flow))
        {
            flow = new OrderFlowAccumulator();
            _orderFlow[signal.Symbol] = flow;
        }

        if (signal.Direction.Equals("BUY", StringComparison.OrdinalIgnoreCase))
        {
            flow.BuyVolume += signal.Volume;
        }
        else
        {
            flow.SellVolume += signal.Volume;
        }
    }

    private void RefreshOrderFlowRows()
    {
        foreach (var (symbol, flow) in _orderFlow)
        {
            var total = Math.Max(1, flow.BuyVolume + flow.SellVolume);
            var ratio = (double)flow.BuyVolume / total;
            var buyPixels = Math.Round(160.0 * ratio, 1);
            var sellPixels = Math.Round(160.0 - buyPixels, 1);

            if (_orderFlowRowsBySymbol.TryGetValue(symbol, out var row))
            {
                row.Update(buyPixels, sellPixels, ratio);
            }
            else
            {
                _orderFlowRowsBySymbol[symbol] = new OrderFlowRowViewModel(symbol, buyPixels, sellPixels, ratio);
            }
        }

        var ordered = _orderFlowRowsBySymbol.Values.OrderByDescending(x => x.BuyPixels).Take(8).ToArray();
        OrderFlowRows.Clear();
        foreach (var row in ordered)
        {
            OrderFlowRows.Add(row);
        }
    }

    private async Task RefreshMetricsAsync()
    {
        try
        {
            var queueStats = await _apiClient.GetQueueStatsAsync(BackendBaseUrl);
            if (queueStats is not null)
            {
                ProcessedCount = queueStats.Processed;
                DroppedCount = queueStats.Dropped;
                QueueDepth = queueStats.QueueDepth;
            }

            var systemMetrics = await _apiClient.GetSystemMetricsAsync(BackendBaseUrl);
            if (systemMetrics is not null)
            {
                CpuUsageText = $"{systemMetrics.CpuUsagePercent:0.#}%";
                MemoryUsageText = FormatBytes(systemMetrics.MemoryUsageBytes);
                WorkingSetText = FormatBytes(systemMetrics.WorkingSetBytes);
                ThreadCount = systemMetrics.ThreadCount;
            }
        }
        catch
        {
            // Keep UI responsive even when metrics endpoints temporarily fail.
        }
    }

    private async Task RefreshTradingPanelsAsync()
    {
        try
        {
            var depth = await _apiClient.GetDepthAsync(BackendBaseUrl, SelectedSymbol);
            ApplyDepth(depth, _lastOrderResult?.ConsumedLiquidity);

            var positions = await _apiClient.GetPositionsAsync(BackendBaseUrl) ?? [];
            PositionSummary.Update(SelectedSymbol, positions.FirstOrDefault(p => p.Symbol.Equals(SelectedSymbol, StringComparison.OrdinalIgnoreCase)));

            _currentStats = await _apiClient.GetExecutionStatsAsync(BackendBaseUrl);
            PortfolioStats.Update(positions, _currentStats);
            ExecutionStats.Update(_currentStats, _currentDepth, _lastOrderResult);

            MarketMakerSummary.Update(await _apiClient.GetMarketMakerStateAsync(BackendBaseUrl));
            ApplyTradeMonitor(await _apiClient.GetTradeMonitorAsync(BackendBaseUrl));
        }
        catch
        {
            // Trading panels are supplementary; leave previous values visible on transient failures.
        }
    }

    private void ApplyHubStats(HubStatsDto stats)
    {
        ProcessedCount = stats.ProcessedTotal;
        DroppedCount = stats.DroppedTotal;
        QueueDepth = stats.QueueDepth;
        ServerRate = stats.ServerGenerationRatePerSec;
        SnapshotsPerSec = stats.SnapshotsPerSec;
        ServerGeneratedTotal = stats.ServerGeneratedTotal;
        CoalescedPerSec = stats.CoalescedPerSec;
    }

    private void ApplyDepth(DepthSnapshotDto? depth, IReadOnlyList<FillDto>? consumedLiquidity)
    {
        if (depth is null)
        {
            return;
        }

        _currentDepth = depth;
        AskDepthRows.Clear();
        foreach (var level in depth.Asks.OrderByDescending(x => x.Price).Take(12))
        {
            AskDepthRows.Add(new DepthLevelViewModel("ASK", level.Price, level.Quantity, IsConsumed(level, consumedLiquidity)));
        }

        BidDepthRows.Clear();
        foreach (var level in depth.Bids.OrderByDescending(x => x.Price).Take(12))
        {
            BidDepthRows.Add(new DepthLevelViewModel("BID", level.Price, level.Quantity, IsConsumed(level, consumedLiquidity)));
        }

        MatchingModelText = $"BOOK {depth.Sequence:N0} · FIFO PRICE-TIME";
        DepthMidText = $"MID {depth.MidPrice:N2}";
        if (_lastOrderResult?.Order?.AverageFillPrice is null && _lastOrderResult?.Fill?.Price is null)
        {
            AverageFillBadgeText = $"FILL AVG {depth.MidPrice:N2}";
        }
        ExecutionStats.Update(_currentStats, depth, _lastOrderResult);
    }

    private void ApplyOrderResult(OrderResultDto? result)
    {
        if (result is null)
        {
            return;
        }

        ApplyDepth(result.Depth, result.ConsumedLiquidity);
        ApplyLifecycle(result.LifecycleEvents);
        _currentStats = result.Stats ?? _currentStats;
        ExecutionStats.Update(_currentStats, _currentDepth, result);

        if (result.ConsumedLiquidity?.Count > 0)
        {
            var total = result.ConsumedLiquidity.Sum(fill => fill.Quantity);
            var consumedText = string.Join(", ", result.ConsumedLiquidity.Take(8).Select(fill => $"{fill.Quantity:N0} @ {fill.Price:N2}"));
            ConsumedLiquidityText = $"Recent consumed: {total:N0} total ({consumedText})";
        }

        var avgFill = result.Order?.AverageFillPrice ?? result.Fill?.Price;
        AverageFillBadgeText = avgFill is null ? "FILL AVG —" : $"FILL AVG {avgFill:N2}";
    }

    private void ApplyLifecycle(IReadOnlyList<OrderLifecycleEventDto>? events)
    {
        if (events is null)
        {
            return;
        }

        var appended = false;
        foreach (var row in events.Select(evt => new LifecycleRowViewModel(evt)))
        {
            if (!_lifecycleEventIds.Add(row.EventId))
            {
                continue;
            }

            LifecycleRows.Add(row);
            appended = true;
        }

        while (LifecycleRows.Count > MaxLifecycleRows)
        {
            _lifecycleEventIds.Remove(LifecycleRows[0].EventId);
            LifecycleRows.RemoveAt(0);
        }

        if (appended)
        {
            LifecycleAppended?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyTradeMonitor(IReadOnlyList<TradeMonitorRowDto>? rows)
    {
        if (rows is null)
        {
            return;
        }

        TradeMonitorRows.Clear();
        foreach (var row in rows.Take(MaxTradeMonitorRows).Select(x => new TradeMonitorRowViewModel(x)))
        {
            TradeMonitorRows.Add(row);
        }
    }

    private void UpdateMessageRate()
    {
        ClientMsgPerSec = _msgCounter;
        OnPropertyChanged(nameof(CurrentRateText));

        _rateHistory[_rateHistoryIndex] = _msgCounter;
        _rateHistoryIndex = (_rateHistoryIndex + 1) % _rateHistory.Length;
        if (_rateHistoryIndex == 0)
        {
            _rateHistoryFilled = true;
        }

        UpdateRateAxisLabels();
        RefreshRateSamples();
        _msgCounter = 0;
    }

    private void RefreshRateSamples()
    {
        var count = _rateHistoryFilled ? _rateHistory.Length : _rateHistoryIndex;
        RateSamples.Clear();
        for (var i = 0; i < count; i++)
        {
            var idx = (_rateHistoryIndex - count + i + _rateHistory.Length) % _rateHistory.Length;
            RateSamples.Add(_rateHistory[idx]);
        }
    }

    private void UpdateRateAxisLabels()
    {
        var elapsedSeconds = Math.Min(_rateHistory.Length, _rateHistoryFilled ? _rateHistory.Length : _rateHistoryIndex);
        RateAxisLabel0 = $"-{elapsedSeconds}s";
        RateAxisLabel1 = $"-{Math.Round(elapsedSeconds * 0.75):0}s";
        RateAxisLabel2 = $"-{Math.Round(elapsedSeconds * 0.5):0}s";
        RateAxisLabel3 = $"-{Math.Round(elapsedSeconds * 0.25):0}s";
    }

    private void SetConnectionStatus(string status)
    {
        ConnectionStatusText = status;
        ConnectionStatusBrush = status switch
        {
            "CONNECTED" => TerminalBrushes.Positive,
            "CONNECTING" or "RECONNECTING" => TerminalBrushes.Yellow,
            _ => TerminalBrushes.Negative
        };
        IsConnected = status == "CONNECTED";
    }

    private void AddLifecycleMessage(string stage, string message)
    {
        var evt = new OrderLifecycleEventDto
        {
            EventId = Guid.NewGuid(),
            OrderId = Guid.Empty,
            Stage = stage,
            Message = message,
            Symbol = SelectedSymbol,
            Timestamp = DateTime.UtcNow
        };
        ApplyLifecycle([evt]);
    }

    private void RaiseCommandStates()
    {
        SubmitBuyCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanSubmitBuy));
        OnPropertyChanged(nameof(IsOrderSubmitInFlight));
    }

    private static bool IsConsumed(DepthLevelDto level, IReadOnlyList<FillDto>? fills) =>
        fills?.Any(fill => fill.Price == level.Price) == true;

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 MB";
        }

        return bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB"
            : $"{bytes / 1024.0 / 1024:0.0} MB";
    }
}
