using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.AspNetCore.SignalR.Client;

namespace TradeDemo.Wpf;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int MaxFeedRows = 220;
    private const int MaxFeedRowsPerFrame = 24;

    private static readonly HttpClient Http = new();

    private readonly ConcurrentQueue<TradeSignal> _pendingSignals = new();
    private readonly Dictionary<string, PriceRow> _priceBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TickerRow> _tickerBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OrderFlowAccumulator> _orderFlow = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OrderFlowRow> _orderFlowRowsBySymbol = new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _msgRateTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private HubConnection? _hubConnection;
    private long _processed;
    private long _dropped;
    private int _queueDepth;
    private int _clientMsgPerSec;
    private int _msgCounter;
    private string _clockText = DateTime.Now.ToString("HH:mm:ss.fff");
    private string _connectionStatusText = "DISCONNECTED";
    private Brush _connectionStatusBrush = Brushes.OrangeRed;
    private string _backendBaseUrl = "https://tradedemo-app-mln7un.proudbush-f48dd93b.australiaeast.azurecontainerapps.io";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PriceRow> PriceRows { get; } = [];
    public ObservableCollection<FeedRow> FeedRows { get; } = [];
    public ObservableCollection<TickerRow> TickerItems { get; } = [];
    public ObservableCollection<OrderFlowRow> OrderFlowRows { get; } = [];

    public string BackendBaseUrl
    {
        get => _backendBaseUrl;
        set
        {
            if (_backendBaseUrl == value)
            {
                return;
            }

            _backendBaseUrl = value.TrimEnd('/');
            OnPropertyChanged();
        }
    }

    public long ProcessedCount
    {
        get => _processed;
        private set
        {
            _processed = value;
            OnPropertyChanged();
        }
    }

    public long DroppedCount
    {
        get => _dropped;
        private set
        {
            _dropped = value;
            OnPropertyChanged();
        }
    }

    public int QueueDepth
    {
        get => _queueDepth;
        private set
        {
            _queueDepth = value;
            OnPropertyChanged();
        }
    }

    public int ClientMsgPerSec
    {
        get => _clientMsgPerSec;
        private set
        {
            _clientMsgPerSec = value;
            OnPropertyChanged();
        }
    }

    public int FeedCount => FeedRows.Count;

    public string ClockText
    {
        get => _clockText;
        private set
        {
            _clockText = value;
            OnPropertyChanged();
        }
    }

    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        private set
        {
            _connectionStatusText = value;
            OnPropertyChanged();
        }
    }

    public Brush ConnectionStatusBrush
    {
        get => _connectionStatusBrush;
        private set
        {
            _connectionStatusBrush = value;
            OnPropertyChanged();
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _clockTimer.Tick += (_, _) => ClockText = DateTime.Now.ToString("HH:mm:ss.fff");
        _clockTimer.Start();

        _msgRateTimer.Tick += (_, _) =>
        {
            ClientMsgPerSec = _msgCounter;
            _msgCounter = 0;
        };
        _msgRateTimer.Start();

        _statsTimer.Tick += async (_, _) => await PollQueueStatsAsync();

        _renderTimer.Tick += (_, _) => DrainPendingSignals();
        _renderTimer.Start();

        Loaded += async (_, _) => await ConnectAsync();
        Closing += async (_, _) => await DisconnectAsync();
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        await ConnectAsync();
    }

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectAsync();
    }

    private async Task ConnectAsync()
    {
        await DisconnectAsync();

        while (_pendingSignals.TryDequeue(out _))
        {
        }

        SetConnectionStatus("CONNECTING", Brushes.Gold);
        var hubUrl = $"{BackendBaseUrl}/tradehub";

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.Reconnecting += _ =>
        {
            Dispatcher.Invoke(() => SetConnectionStatus("RECONNECTING", Brushes.Gold));
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += _ =>
        {
            Dispatcher.Invoke(() => SetConnectionStatus("CONNECTED", Brushes.LimeGreen));
            return Task.CompletedTask;
        };

        _hubConnection.Closed += _ =>
        {
            Dispatcher.Invoke(() => SetConnectionStatus("DISCONNECTED", Brushes.OrangeRed));
            return Task.CompletedTask;
        };

        _hubConnection.On<TradeSignal>("TradeSignal", signal => _pendingSignals.Enqueue(signal));

        _hubConnection.On<List<TradeSignal>>("TradeSignalBatch", batch =>
        {
            foreach (var signal in batch)
            {
                _pendingSignals.Enqueue(signal);
            }
        });

        await _hubConnection.StartAsync();

        SetConnectionStatus("CONNECTED", Brushes.LimeGreen);
        _statsTimer.Start();
        await PollQueueStatsAsync();
    }

    private async Task DisconnectAsync()
    {
        _statsTimer.Stop();

        if (_hubConnection is null)
        {
            SetConnectionStatus("DISCONNECTED", Brushes.OrangeRed);
            return;
        }

        try
        {
            await _hubConnection.StopAsync();
            await _hubConnection.DisposeAsync();
        }
        catch
        {
            // Ignore shutdown errors during close or transient network failures.
        }
        finally
        {
            _hubConnection = null;
            SetConnectionStatus("DISCONNECTED", Brushes.OrangeRed);
        }
    }

    private async Task PollQueueStatsAsync()
    {
        try
        {
            var stats = await Http.GetFromJsonAsync<QueueStatsResponse>($"{BackendBaseUrl}/api/queue/stats");
            if (stats is null)
            {
                return;
            }

            ProcessedCount = stats.Processed;
            DroppedCount = stats.Dropped;
            QueueDepth = stats.QueueDepth;
        }
        catch
        {
            // Keep UI responsive even when stats endpoint temporarily fails.
        }
    }

    private void DrainPendingSignals()
    {
        if (_pendingSignals.IsEmpty)
        {
            return;
        }

        var latestBySymbol = new Dictionary<string, TradeSignal>(StringComparer.OrdinalIgnoreCase);
        var feedWindow = new Queue<TradeSignal>(MaxFeedRowsPerFrame);
        var dequeued = 0;

        while (_pendingSignals.TryDequeue(out var signal))
        {
            dequeued++;
            latestBySymbol[signal.Symbol] = signal;
            RecordOrderFlow(signal);

            if (feedWindow.Count >= MaxFeedRowsPerFrame)
            {
                feedWindow.Dequeue();
            }

            feedWindow.Enqueue(signal);
        }

        _msgCounter += dequeued;

        foreach (var signal in latestBySymbol.Values)
        {
            UpdatePriceAndTicker(signal);
        }

        foreach (var signal in feedWindow.Reverse())
        {
            AppendFeed(signal);
        }

        RefreshOrderFlowRows();
    }

    private void UpdatePriceAndTicker(TradeSignal signal)
    {
        if (!_priceBySymbol.TryGetValue(signal.Symbol, out var priceRow))
        {
            priceRow = new PriceRow(signal.Symbol, signal.Exchange, signal.Price, signal.ChangePercent);
            _priceBySymbol[signal.Symbol] = priceRow;
            PriceRows.Insert(0, priceRow);
        }
        else
        {
            priceRow.Update(signal.Price, signal.ChangePercent, signal.Exchange);
        }

        if (!_tickerBySymbol.TryGetValue(signal.Symbol, out var tickerRow))
        {
            tickerRow = new TickerRow(signal.Symbol, signal.Price, signal.ChangePercent);
            _tickerBySymbol[signal.Symbol] = tickerRow;
            TickerItems.Add(tickerRow);
        }
        else
        {
            tickerRow.Update(signal.Price, signal.ChangePercent);
        }
    }

    private void AppendFeed(TradeSignal signal)
    {
        var feedRow = new FeedRow(
            signal.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            signal.Symbol,
            signal.Price,
            signal.Volume,
            signal.Direction);

        FeedRows.Insert(0, feedRow);
        while (FeedRows.Count > MaxFeedRows)
        {
            FeedRows.RemoveAt(FeedRows.Count - 1);
        }

        OnPropertyChanged(nameof(FeedCount));
    }

    private void RecordOrderFlow(TradeSignal signal)
    {
        if (!_orderFlow.TryGetValue(signal.Symbol, out var flow))
        {
            flow = new OrderFlowAccumulator();
            _orderFlow[signal.Symbol] = flow;
        }

        if (string.Equals(signal.Direction, "BUY", StringComparison.OrdinalIgnoreCase))
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
        foreach (var kvp in _orderFlow)
        {
            var symbol = kvp.Key;
            var flow = kvp.Value;
            var total = Math.Max(1, flow.BuyVolume + flow.SellVolume);
            var ratio = (double)flow.BuyVolume / total;
            var buyPixels = Math.Round(180.0 * ratio, 1);
            var sellPixels = Math.Round(180.0 - buyPixels, 1);

            if (_orderFlowRowsBySymbol.TryGetValue(symbol, out var row))
            {
                row.Update(buyPixels, sellPixels, ratio);
            }
            else
            {
                row = new OrderFlowRow(symbol, buyPixels, sellPixels, ratio);
                _orderFlowRowsBySymbol[symbol] = row;
            }
        }

        var ordered = _orderFlowRowsBySymbol.Values
            .OrderByDescending(x => x.BuyPixels)
            .Take(9)
            .ToList();

        OrderFlowRows.Clear();
        foreach (var row in ordered)
        {
            OrderFlowRows.Add(row);
        }
    }

    private void SetConnectionStatus(string text, Brush brush)
    {
        ConnectionStatusText = text;
        ConnectionStatusBrush = brush;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class QueueStatsResponse
{
    public long Processed { get; set; }
    public long Dropped { get; set; }
    public int QueueDepth { get; set; }
}

public sealed class TradeSignal
{
    public required string Symbol { get; set; }
    public decimal Price { get; set; }
    public decimal Change { get; set; }
    public double ChangePercent { get; set; }
    public long Volume { get; set; }
    public required string Exchange { get; set; }
    public DateTime Timestamp { get; set; }
    public required string Direction { get; set; }
}

public sealed class PriceRow : INotifyPropertyChanged
{
    private static readonly Brush PositiveBrush = new SolidColorBrush(Color.FromRgb(30, 203, 139));
    private static readonly Brush NegativeBrush = new SolidColorBrush(Color.FromRgb(255, 94, 115));
    private static readonly Brush NeutralBrush = new SolidColorBrush(Color.FromRgb(159, 175, 206));
    private static readonly Brush PositiveRowBrush = new SolidColorBrush(Color.FromRgb(11, 82, 73));
    private static readonly Brush NegativeRowBrush = new SolidColorBrush(Color.FromRgb(87, 26, 41));
    private static readonly Brush NeutralRowBrush = new SolidColorBrush(Color.FromRgb(12, 25, 49));

    private decimal _price;
    private double _changePercent;
    private string _exchange;

    public event PropertyChangedEventHandler? PropertyChanged;

    public PriceRow(string symbol, string exchange, decimal price, double changePercent)
    {
        Symbol = symbol;
        _exchange = exchange;
        _price = price;
        _changePercent = changePercent;
    }

    public string Symbol { get; }

    public string Exchange
    {
        get => _exchange;
        private set
        {
            _exchange = value;
            OnPropertyChanged();
        }
    }

    public string PriceText => _price.ToString("N2", CultureInfo.InvariantCulture);
    public string ChangePercentText => $"{_changePercent:+0.##;-0.##;0.##}%";

    public Brush ChangeBrush => _changePercent switch
    {
        > 0 => PositiveBrush,
        < 0 => NegativeBrush,
        _ => NeutralBrush
    };

    public Brush RowBackgroundBrush => _changePercent switch
    {
        > 0 => PositiveRowBrush,
        < 0 => NegativeRowBrush,
        _ => NeutralRowBrush
    };

    public void Update(decimal price, double changePercent, string exchange)
    {
        _price = price;
        _changePercent = changePercent;
        Exchange = exchange;
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(ChangePercentText));
        OnPropertyChanged(nameof(ChangeBrush));
        OnPropertyChanged(nameof(RowBackgroundBrush));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class FeedRow
{
    private static readonly Brush BuyBrush = new SolidColorBrush(Color.FromRgb(30, 203, 139));
    private static readonly Brush SellBrush = new SolidColorBrush(Color.FromRgb(255, 94, 115));
    private static readonly Brush BuyPriceBrush = new SolidColorBrush(Color.FromRgb(30, 203, 139));
    private static readonly Brush SellPriceBrush = new SolidColorBrush(Color.FromRgb(255, 117, 124));
    private static readonly Brush BuyRowBrush = new SolidColorBrush(Color.FromRgb(9, 43, 57));
    private static readonly Brush SellRowBrush = new SolidColorBrush(Color.FromRgb(45, 28, 49));

    public FeedRow(string timeText, string symbol, decimal price, long volume, string direction)
    {
        TimeText = timeText;
        Symbol = symbol;
        PriceText = price.ToString("N2", CultureInfo.InvariantCulture);
        VolumeText = volume >= 1000 ? $"{volume / 1000.0:0.#}K" : volume.ToString(CultureInfo.InvariantCulture);
        Direction = direction;
    }

    public string TimeText { get; }
    public string Symbol { get; }
    public string PriceText { get; }
    public string VolumeText { get; }
    public string Direction { get; }

    public Brush DirectionBrush => string.Equals(Direction, "BUY", StringComparison.OrdinalIgnoreCase) ? BuyBrush : SellBrush;
    public Brush PriceBrush => string.Equals(Direction, "BUY", StringComparison.OrdinalIgnoreCase) ? BuyPriceBrush : SellPriceBrush;
    public Brush RowBackgroundBrush => string.Equals(Direction, "BUY", StringComparison.OrdinalIgnoreCase) ? BuyRowBrush : SellRowBrush;
}

public sealed class TickerRow : INotifyPropertyChanged
{
    private static readonly Brush PositiveBrush = new SolidColorBrush(Color.FromRgb(30, 203, 139));
    private static readonly Brush NegativeBrush = new SolidColorBrush(Color.FromRgb(255, 94, 115));
    private static readonly Brush NeutralBrush = new SolidColorBrush(Color.FromRgb(159, 175, 206));

    private decimal _price;
    private double _changePercent;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TickerRow(string symbol, decimal price, double changePercent)
    {
        Symbol = symbol;
        _price = price;
        _changePercent = changePercent;
    }

    public string Symbol { get; }
    public string PriceText => _price.ToString("N2", CultureInfo.InvariantCulture);
    public string ChangePercentText => $"{_changePercent:+0.##;-0.##;0.##}%";

    public Brush ChangeBrush => _changePercent switch
    {
        > 0 => PositiveBrush,
        < 0 => NegativeBrush,
        _ => NeutralBrush
    };

    public void Update(decimal price, double changePercent)
    {
        _price = price;
        _changePercent = changePercent;
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(ChangePercentText));
        OnPropertyChanged(nameof(ChangeBrush));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class OrderFlowRow : INotifyPropertyChanged
{
    private double _buyPixels;
    private double _sellPixels;
    private double _buyRatio;

    public event PropertyChangedEventHandler? PropertyChanged;

    public OrderFlowRow(string symbol, double buyPixels, double sellPixels, double buyRatio)
    {
        Symbol = symbol;
        _buyPixels = buyPixels;
        _sellPixels = sellPixels;
        _buyRatio = buyRatio;
    }

    public string Symbol { get; }
    public double BuyPixels => _buyPixels;
    public double SellPixels => _sellPixels;
    public string BuyRatioText => $"{Math.Round(_buyRatio * 100.0):0}%";

    public void Update(double buyPixels, double sellPixels, double buyRatio)
    {
        _buyPixels = buyPixels;
        _sellPixels = sellPixels;
        _buyRatio = buyRatio;
        OnPropertyChanged(nameof(BuyPixels));
        OnPropertyChanged(nameof(SellPixels));
        OnPropertyChanged(nameof(BuyRatioText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class OrderFlowAccumulator
{
    public long BuyVolume { get; set; }
    public long SellVolume { get; set; }
}
