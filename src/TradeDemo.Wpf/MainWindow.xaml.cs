using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using TradeDemo.Wpf.Services;
using TradeDemo.Wpf.ViewModels;

namespace TradeDemo.Wpf;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _tickerMarqueeTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private bool _isTickerResetPending;

    public MainWindow()
    {
        InitializeComponent();

        var apiClient = new TradeApiClient(new HttpClient());
        var hubClient = new TradeHubClient(Dispatcher);
        _viewModel = new MainWindowViewModel(apiClient, hubClient, new TerminalFeedProcessor());
        _viewModel.LifecycleAppended += (_, _) =>
            Dispatcher.BeginInvoke(() =>
            {
                if (_viewModel.LifecycleRows.Count > 0)
                {
                    LifecycleGrid.ScrollIntoView(_viewModel.LifecycleRows[^1]);
                }
            }, DispatcherPriority.Background);
        DataContext = _viewModel;

        _tickerMarqueeTimer.Tick += (_, _) => AdvanceTickerMarquee();
        Loaded += async (_, _) =>
        {
            _tickerMarqueeTimer.Start();
            await _viewModel.StartAsync();
        };
        Closing += async (_, _) =>
        {
            _tickerMarqueeTimer.Stop();
            await _viewModel.StopAsync();
        };
    }

    private void AdvanceTickerMarquee()
    {
        if (TickerScrollViewer.ScrollableWidth <= 0)
        {
            return;
        }

        if (_isTickerResetPending)
        {
            TickerScrollViewer.ScrollToHorizontalOffset(0);
            _isTickerResetPending = false;
            return;
        }

        var nextOffset = TickerScrollViewer.HorizontalOffset + 1.2;
        if (nextOffset >= TickerScrollViewer.ScrollableWidth)
        {
            _isTickerResetPending = true;
            return;
        }

        TickerScrollViewer.ScrollToHorizontalOffset(nextOffset);
    }
}
