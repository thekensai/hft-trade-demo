using Microsoft.AspNetCore.SignalR.Client;
using System.Windows.Threading;
using TradeDemo.Wpf.Models;

namespace TradeDemo.Wpf.Services;

public sealed class TradeHubClient : IAsyncDisposable
{
    private readonly Dispatcher _dispatcher;
    private HubConnection? _connection;

    public TradeHubClient(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public event Action<TradeSignalDto>? TradeSignalReceived;
    public event Action<IReadOnlyList<TradeSignalDto>>? TradeSignalsReceived;
    public event Action<HubStatsDto>? StatsReceived;
    public event Action<string>? ConnectionStatusChanged;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string baseUrl)
    {
        await DisconnectAsync();
        RaiseStatus("CONNECTING");

        _connection = new HubConnectionBuilder()
            .WithUrl($"{baseUrl.TrimEnd('/')}/tradehub")
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)])
            .Build();

        _connection.Reconnecting += _ =>
        {
            Dispatch(() => RaiseStatus("RECONNECTING"));
            return Task.CompletedTask;
        };

        _connection.Reconnected += _ =>
        {
            Dispatch(() => RaiseStatus("CONNECTED"));
            return Task.CompletedTask;
        };

        _connection.Closed += _ =>
        {
            Dispatch(() => RaiseStatus("DISCONNECTED"));
            return Task.CompletedTask;
        };

        _connection.On<TradeSignalDto>("TradeSignal", signal => Dispatch(() => TradeSignalReceived?.Invoke(signal)));
        _connection.On<List<TradeSignalDto>>("TradeSignals", signals => Dispatch(() => TradeSignalsReceived?.Invoke(signals)));
        _connection.On<List<TradeSignalDto>>("TradeSignalBatch", signals => Dispatch(() => TradeSignalsReceived?.Invoke(signals)));
        _connection.On<HubStatsDto>("Stats", stats => Dispatch(() => StatsReceived?.Invoke(stats)));

        await _connection.StartAsync();
        RaiseStatus("CONNECTED");
    }

    public async Task DisconnectAsync()
    {
        if (_connection is null)
        {
            RaiseStatus("DISCONNECTED");
            return;
        }

        try
        {
            await _connection.StopAsync();
            await _connection.DisposeAsync();
        }
        catch
        {
            // Ignore shutdown errors during close or transient network failures.
        }
        finally
        {
            _connection = null;
            RaiseStatus("DISCONNECTED");
        }
    }

    public ValueTask DisposeAsync() => _connection?.DisposeAsync() ?? ValueTask.CompletedTask;

    private void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    private void RaiseStatus(string status) => ConnectionStatusChanged?.Invoke(status);
}
