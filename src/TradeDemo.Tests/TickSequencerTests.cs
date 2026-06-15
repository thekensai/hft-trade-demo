using TradeDemo.Api.Services;

namespace TradeDemo.Tests;

public sealed class TickSequencerTests
{
    [Fact]
    public void Current_StartsAtZero()
    {
        var sequencer = new TickSequencer();

        Assert.Equal(0, sequencer.Current);
    }

    [Fact]
    public void Next_ReturnsMonotonicSequenceNumbers()
    {
        var sequencer = new TickSequencer();

        Assert.Equal(1, sequencer.Next());
        Assert.Equal(2, sequencer.Next());
        Assert.Equal(2, sequencer.Current);
    }

    [Fact]
    public async Task Next_ReturnsUniqueSequenceNumbersAcrossConcurrentCalls()
    {
        var sequencer = new TickSequencer();
        var tasks = Enumerable.Range(0, 1_000)
            .Select(_ => Task.Run(sequencer.Next))
            .ToArray();

        var values = await Task.WhenAll(tasks);

        Assert.Equal(1_000, values.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 1_000).Select(i => (long)i), values.Order());
        Assert.Equal(1_000, sequencer.Current);
    }
}
