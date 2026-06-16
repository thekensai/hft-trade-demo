using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using TradeDemo.Wpf.Models;

namespace TradeDemo.Wpf.ViewModels;

public static class TerminalBrushes
{
    public static readonly Brush Positive = Frozen(16, 185, 129);
    public static readonly Brush Negative = Frozen(239, 68, 68);
    public static readonly Brush Neutral = Frozen(148, 163, 184);
    public static readonly Brush Cyan = Frozen(6, 182, 212);
    public static readonly Brush Yellow = Frozen(245, 158, 11);
    public static readonly Brush TextPrimary = Frozen(226, 232, 240);
    public static readonly Brush PanelAlt = Frozen(17, 24, 39);
    public static readonly Brush ConsumedRow = Frozen(78, 60, 31);
    public static readonly Brush HealthyTradeRow = Frozen(14, 42, 42);
    public static readonly Brush SlowTradeRow = Frozen(44, 37, 26);
    public static readonly Brush AbnormalTradeRow = Frozen(48, 27, 35);
    public static readonly Brush SelectedRow = Frozen(18, 56, 74);
    public static readonly Brush PositiveRow = Frozen(6, 95, 70);
    public static readonly Brush NegativeRow = Frozen(127, 29, 29);
    public static readonly Brush MutedRow = Frozen(17, 24, 39);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class PriceRowViewModel : ViewModelBase
{
    private decimal _midPrice;
    private decimal _bidPrice;
    private decimal _askPrice;
    private double _changePercent;
    private string _exchange;
    private bool _isSelected;

    public PriceRowViewModel(string symbol, string exchange, decimal midPrice, decimal bidPrice, decimal askPrice, double changePercent)
    {
        Symbol = symbol;
        _exchange = exchange;
        _midPrice = midPrice;
        _bidPrice = bidPrice;
        _askPrice = askPrice;
        _changePercent = changePercent;
    }

    public string Symbol { get; }
    public string Exchange { get => _exchange; private set => SetProperty(ref _exchange, value); }
    public bool IsSelected { get => _isSelected; set { if (SetProperty(ref _isSelected, value)) OnPropertyChanged(nameof(RowBackgroundBrush)); } }
    public string BidAskText => $"{_bidPrice:N2} / {_askPrice:N2}";
    public string MidPriceText => _midPrice.ToString("N2", CultureInfo.InvariantCulture);
    public string ChangePercentText => $"{_changePercent:+0.##;-0.##;0.##}%";
    public Brush ChangeBrush => ChangeBrushFor(_changePercent);
    public Brush RowBackgroundBrush => IsSelected ? TerminalBrushes.SelectedRow : _changePercent >= 0 ? TerminalBrushes.PositiveRow : TerminalBrushes.NegativeRow;

    public void Update(decimal midPrice, decimal bidPrice, decimal askPrice, double changePercent, string exchange)
    {
        _midPrice = midPrice;
        _bidPrice = bidPrice;
        _askPrice = askPrice;
        _changePercent = changePercent;
        Exchange = exchange;
        OnPropertyChanged(nameof(BidAskText));
        OnPropertyChanged(nameof(MidPriceText));
        OnPropertyChanged(nameof(ChangePercentText));
        OnPropertyChanged(nameof(ChangeBrush));
        OnPropertyChanged(nameof(RowBackgroundBrush));
    }

    internal static Brush ChangeBrushFor(double changePercent) => changePercent switch
    {
        > 0 => TerminalBrushes.Positive,
        < 0 => TerminalBrushes.Negative,
        _ => TerminalBrushes.Neutral
    };
}

public sealed class FeedRowViewModel
{
    public FeedRowViewModel(TradeSignalDto signal)
    {
        TimeText = signal.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Symbol = signal.Symbol;
        var execPrice = signal.Direction.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? signal.AskPrice : signal.BidPrice;
        PriceText = execPrice.ToString("N2", CultureInfo.InvariantCulture);
        BidAskFeedText = $"{signal.BidPrice:N2}/{signal.AskPrice:N2}";
        VolumeText = signal.Volume >= 1000 ? $"{signal.Volume / 1000.0:0.#}K" : signal.Volume.ToString(CultureInfo.InvariantCulture);
        Direction = signal.Direction;
    }

    public string TimeText { get; }
    public string Symbol { get; }
    public string PriceText { get; }
    public string BidAskFeedText { get; }
    public string VolumeText { get; }
    public string Direction { get; }
    public Brush DirectionBrush => IsBuy ? TerminalBrushes.Positive : TerminalBrushes.Negative;
    public Brush PriceBrush => DirectionBrush;
    public Brush RowBackgroundBrush => IsBuy ? TerminalBrushes.PositiveRow : TerminalBrushes.NegativeRow;
    private bool IsBuy => Direction.Equals("BUY", StringComparison.OrdinalIgnoreCase);
}

public sealed class TickerRowViewModel : ViewModelBase
{
    private decimal _price;
    private double _changePercent;

    public TickerRowViewModel(string symbol, decimal price, double changePercent)
    {
        Symbol = symbol;
        _price = price;
        _changePercent = changePercent;
    }

    public string Symbol { get; }
    public string PriceText => _price.ToString("N2", CultureInfo.InvariantCulture);
    public string ChangePercentText => $"{_changePercent:+0.##;-0.##;0.##}%";
    public Brush ChangeBrush => PriceRowViewModel.ChangeBrushFor(_changePercent);

    public void Update(decimal price, double changePercent)
    {
        _price = price;
        _changePercent = changePercent;
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(ChangePercentText));
        OnPropertyChanged(nameof(ChangeBrush));
    }
}

public sealed class OrderFlowRowViewModel : ViewModelBase
{
    private double _buyPixels;
    private double _sellPixels;
    private double _buyRatio;

    public OrderFlowRowViewModel(string symbol, double buyPixels, double sellPixels, double buyRatio)
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
}

public sealed class DepthLevelViewModel
{
    public DepthLevelViewModel(string side, decimal price, int quantity, bool consumed = false)
    {
        Side = side;
        PriceText = price.ToString("N2", CultureInfo.InvariantCulture);
        QuantityText = quantity.ToString("N0", CultureInfo.InvariantCulture);
        RowBrush = consumed ? TerminalBrushes.ConsumedRow : TerminalBrushes.MutedRow;
        SideBrush = consumed ? TerminalBrushes.Yellow : side == "ASK" ? TerminalBrushes.Negative : TerminalBrushes.Positive;
        HitText = consumed ? "hit" : string.Empty;
    }

    public string Side { get; }
    public string PriceText { get; }
    public string QuantityText { get; }
    public string HitText { get; }
    public Brush RowBrush { get; }
    public Brush SideBrush { get; }
    public Brush PriceBrush => SideBrush;
}

public sealed class LifecycleRowViewModel
{
    public LifecycleRowViewModel(OrderLifecycleEventDto evt)
    {
        EventId = evt.EventId;
        TimeText = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        Symbol = evt.Symbol ?? "—";
        Stage = evt.Stage;
        Message = evt.Message;
        StageBrush = Stage.Contains("Reject", StringComparison.OrdinalIgnoreCase) || Stage.Contains("Cancel", StringComparison.OrdinalIgnoreCase) ? TerminalBrushes.Negative :
            Stage.Contains("Route", StringComparison.OrdinalIgnoreCase) ? TerminalBrushes.Yellow :
            Stage.Contains("Fill", StringComparison.OrdinalIgnoreCase) ? TerminalBrushes.Positive : TerminalBrushes.Cyan;
        AccentBrush = StageBrush;
    }

    public Guid EventId { get; }
    public string TimeText { get; }
    public string Symbol { get; }
    public string Stage { get; }
    public string Message { get; }
    public Brush StageBrush { get; }
    public Brush AccentBrush { get; }
}

public sealed class TradeMonitorRowViewModel
{
    public TradeMonitorRowViewModel(TradeMonitorRowDto row)
    {
        OrderIdText = row.OrderId == Guid.Empty ? "—" : row.OrderId.ToString("N")[..8];
        Symbol = row.Symbol;
        Status = row.Status;
        Venue = row.Venue;
        LatencyText = row.LatencyMs is null ? "—" : $"{row.LatencyMs:0.0}ms";
        FillText = $"{row.FillPercent:0}%";
        PnLText = TerminalFormat.Money(row.PnL);
        PnLBrush = row.PnL < 0 ? TerminalBrushes.Negative : row.PnL > 0 ? TerminalBrushes.Positive : TerminalBrushes.TextPrimary;
        Health = string.IsNullOrWhiteSpace(row.Health) ? "healthy" : row.Health;
        HealthBrush = Health.Equals("abnormal", StringComparison.OrdinalIgnoreCase) ? TerminalBrushes.Negative :
            Health.Equals("slow", StringComparison.OrdinalIgnoreCase) ? TerminalBrushes.Yellow : TerminalBrushes.Positive;
        RowBrush = Health.Equals("abnormal", StringComparison.OrdinalIgnoreCase) ? TerminalBrushes.AbnormalTradeRow :
            Health.Equals("slow", StringComparison.OrdinalIgnoreCase) ? TerminalBrushes.SlowTradeRow : TerminalBrushes.HealthyTradeRow;
    }

    public string OrderIdText { get; }
    public string Symbol { get; }
    public string Status { get; }
    public string Venue { get; }
    public string LatencyText { get; }
    public string FillText { get; }
    public string PnLText { get; }
    public string Health { get; }
    public Brush HealthBrush { get; }
    public Brush PnLBrush { get; }
    public Brush RowBrush { get; }
}

public sealed class MetricTileViewModel : ViewModelBase
{
    private string _value;

    public MetricTileViewModel(string label, string value)
    {
        Label = label;
        _value = value;
    }

    public string Label { get; }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
}

public sealed class PositionSummaryViewModel : ViewModelBase
{
    private string _symbol = "ES";
    private string _quantityText = "—";
    private string _averagePriceText = "—";
    private string _realizedPnlText = "—";
    private string _unrealizedPnlText = "—";

    public string Symbol { get => _symbol; set => SetProperty(ref _symbol, value); }
    public string QuantityText { get => _quantityText; set => SetProperty(ref _quantityText, value); }
    public string AveragePriceText { get => _averagePriceText; set => SetProperty(ref _averagePriceText, value); }
    public string RealizedPnlText { get => _realizedPnlText; set => SetProperty(ref _realizedPnlText, value); }
    public string UnrealizedPnlText { get => _unrealizedPnlText; set => SetProperty(ref _unrealizedPnlText, value); }

    public void Update(string symbol, PositionDto? position)
    {
        Symbol = symbol;
        QuantityText = position?.Quantity.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        AveragePriceText = position is null ? "—" : position.AveragePrice.ToString("N2", CultureInfo.InvariantCulture);
        RealizedPnlText = position is null ? "—" : TerminalFormat.Money(position.RealizedPnl);
        UnrealizedPnlText = position is null ? "—" : TerminalFormat.Money(position.UnrealizedPnl);
    }
}

public sealed class MarketMakerSummaryViewModel : ViewModelBase
{
    private string _inventoryText = "—";
    private string _limitText = "100";
    private string _statusText = "OFF";
    private string _quotesText = "—/—";

    public string InventoryText { get => _inventoryText; set => SetProperty(ref _inventoryText, value); }
    public string LimitText { get => _limitText; set => SetProperty(ref _limitText, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string QuotesText { get => _quotesText; set => SetProperty(ref _quotesText, value); }

    public void Update(MarketMakerStateDto? state)
    {
        if (state is null) return;
        InventoryText = "—";
        LimitText = state.InventoryLimit.ToString("N0", CultureInfo.InvariantCulture);
        StatusText = "OFF";
        QuotesText = "—/—";
    }
}

public sealed class ExecutionStatsViewModel : ViewModelBase
{
    private string _ordersSentText = "0";
    private string _ordersFilledText = "0";
    private string _rejectedText = "0";
    private string _fillRatioText = "0%";
    private string _cancelsText = "0";
    private string _lastOrderText = "READY";
    private string _markMidText = "—";
    private string _arrivalText = "—";
    private string _averageFillText = "—";
    private string _slippageText = "—";
    private string _totalLatencyText = "0ms";
    private string _riskLatencyText = "0ms";
    private string _routeLatencyText = "0ms";
    private string _exchangeLatencyText = "0ms";
    private string _fillLatencyText = "0ms";
    private string _otherLatencyText = "0ms";

    public string OrdersSentText { get => _ordersSentText; set => SetProperty(ref _ordersSentText, value); }
    public string OrdersFilledText { get => _ordersFilledText; set => SetProperty(ref _ordersFilledText, value); }
    public string RejectedText { get => _rejectedText; set => SetProperty(ref _rejectedText, value); }
    public string FillRatioText { get => _fillRatioText; set => SetProperty(ref _fillRatioText, value); }
    public string CancelsText { get => _cancelsText; set => SetProperty(ref _cancelsText, value); }
    public string LastOrderText { get => _lastOrderText; set => SetProperty(ref _lastOrderText, value); }
    public string MarkMidText { get => _markMidText; set => SetProperty(ref _markMidText, value); }
    public string ArrivalText { get => _arrivalText; set => SetProperty(ref _arrivalText, value); }
    public string AverageFillText { get => _averageFillText; set => SetProperty(ref _averageFillText, value); }
    public string SlippageText { get => _slippageText; set => SetProperty(ref _slippageText, value); }
    public string TotalLatencyText { get => _totalLatencyText; set => SetProperty(ref _totalLatencyText, value); }
    public string RiskLatencyText { get => _riskLatencyText; set => SetProperty(ref _riskLatencyText, value); }
    public string RouteLatencyText { get => _routeLatencyText; set => SetProperty(ref _routeLatencyText, value); }
    public string ExchangeLatencyText { get => _exchangeLatencyText; set => SetProperty(ref _exchangeLatencyText, value); }
    public string FillLatencyText { get => _fillLatencyText; set => SetProperty(ref _fillLatencyText, value); }
    public string OtherLatencyText { get => _otherLatencyText; set => SetProperty(ref _otherLatencyText, value); }

    public void Update(ExecutionStatsDto? stats, DepthSnapshotDto? depth, OrderResultDto? result)
    {
        if (stats is not null)
        {
            OrdersSentText = stats.OrdersSent.ToString("N0", CultureInfo.InvariantCulture);
            OrdersFilledText = stats.OrdersFilled.ToString("N0", CultureInfo.InvariantCulture);
            RejectedText = stats.Rejections.ToString("N0", CultureInfo.InvariantCulture);
            FillRatioText = $"{stats.FillRatio:0.#}%";
            CancelsText = stats.Cancels.ToString("N0", CultureInfo.InvariantCulture);
            AverageFillText = stats.AverageFillPrice == 0 ? "—" : stats.AverageFillPrice.ToString("N2", CultureInfo.InvariantCulture);
        }

        if (depth is not null)
        {
            MarkMidText = depth.MidPrice.ToString("N2", CultureInfo.InvariantCulture);
        }

        if (result?.Order is not null)
        {
            LastOrderText = result.Order.Status;
        }

        if (result?.Slippage is not null)
        {
            ArrivalText = result.Slippage.ArrivalPrice.ToString("N2", CultureInfo.InvariantCulture);
            AverageFillText = result.Slippage.AverageFillPrice.ToString("N2", CultureInfo.InvariantCulture);
            SlippageText = $"{result.Slippage.SlippagePoints:+0.00;-0.00;0.00} / {TerminalFormat.Money(result.Slippage.SlippageDollars)}";
        }

        if (result?.Latency is not null)
        {
            TotalLatencyText = FormatMs(result.Latency.TotalMs);
            RiskLatencyText = FormatMs(result.Latency.RiskCheckMs);
            RouteLatencyText = FormatMs(result.Latency.RouteMs);
            ExchangeLatencyText = FormatMs(result.Latency.ExchangeMs);
            FillLatencyText = FormatMs(result.Latency.FillMs);
            OtherLatencyText = FormatMs(result.Latency.OtherMs);
        }
    }

    private static string FormatMs(double value) => $"{value:0.0}ms";
}

public sealed class PortfolioStatsViewModel : ViewModelBase
{
    private string _netPositionText = "0";
    private string _portfolioMtmText = "$0";
    private string _realizedPnlText = "$0";

    public string NetPositionText { get => _netPositionText; set => SetProperty(ref _netPositionText, value); }
    public string PortfolioMtmText { get => _portfolioMtmText; set => SetProperty(ref _portfolioMtmText, value); }
    public string RealizedPnlText { get => _realizedPnlText; set => SetProperty(ref _realizedPnlText, value); }

    public void Update(IEnumerable<PositionDto> positions, ExecutionStatsDto? stats)
    {
        var list = positions.ToArray();
        NetPositionText = list.Sum(p => p.Quantity).ToString("N0", CultureInfo.InvariantCulture);
        PortfolioMtmText = TerminalFormat.Money(stats?.PnL ?? list.Sum(p => p.UnrealizedPnl));
        RealizedPnlText = TerminalFormat.Money(list.Sum(p => p.RealizedPnl));
    }
}

internal sealed class OrderFlowAccumulator
{
    public long BuyVolume { get; set; }
    public long SellVolume { get; set; }
}

internal static class TerminalFormat
{
    public static string Money(decimal value) => value < 0
        ? $"-${Math.Abs(value):N2}"
        : $"${value:N2}";
}
