using System.Diagnostics;

namespace TradeDemo.Api.Services;

public class GenerationStats
{
    private long _totalGenerated;
    private long _lastGeneratedCount;
    private long _lastGeneratedTicks;
    private double _currentGenerationRate;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    public void Increment()
    {
        Interlocked.Increment(ref _totalGenerated);
    }

    public void UpdateRate(long newCount, long newTicks, double newRate)
    {
        Volatile.Write(ref _lastGeneratedCount, newCount);
        Volatile.Write(ref _lastGeneratedTicks, newTicks);
        Volatile.Write(ref _currentGenerationRate, newRate);
    }

    public (long TotalGenerated, double GenerationRatePerSec) GetSnapshot()
    {
        var now = Stopwatch.GetTimestamp();
        var totalElapsed = (now - _startTimestamp) / (double)Stopwatch.Frequency;

        // If we have at least one periodic update, use that recent rate
        if (Volatile.Read(ref _lastGeneratedCount) > 0)
        {
            var elapsed = (now - Volatile.Read(ref _lastGeneratedTicks)) / (double)Stopwatch.Frequency;
            var delta = Volatile.Read(ref _totalGenerated) - Volatile.Read(ref _lastGeneratedCount);
            var rate = elapsed > 0 ? delta / elapsed : Volatile.Read(ref _currentGenerationRate);
            return (Volatile.Read(ref _totalGenerated), rate);
        }

        // Otherwise calculate average rate since start
        var avgRate = totalElapsed > 0 ? Volatile.Read(ref _totalGenerated) / totalElapsed : 0;
        return (Volatile.Read(ref _totalGenerated), avgRate);
    }
}
