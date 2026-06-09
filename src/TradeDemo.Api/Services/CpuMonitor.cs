using System.Diagnostics;
using System.Runtime.InteropServices;

public sealed class CpuMonitor
{
    private readonly Process _process = Process.GetCurrentProcess();

    private TimeSpan _lastCpuTime;
    private long _lastTimestamp;

    private readonly double _containerCpuQuota;

    public CpuMonitor()
    {
        _lastCpuTime = _process.TotalProcessorTime;
        _lastTimestamp = Stopwatch.GetTimestamp();

        _containerCpuQuota = GetContainerCpuQuota();
    }

    public CpuSnapshot Sample()
    {
        _process.Refresh();

        var currentCpuTime = _process.TotalProcessorTime;
        var currentTimestamp = Stopwatch.GetTimestamp();

        var elapsedWallSeconds =
            (currentTimestamp - _lastTimestamp) /
            (double)Stopwatch.Frequency;

        var cpuTimeSeconds =
            (currentCpuTime - _lastCpuTime).TotalSeconds;

        _lastCpuTime = currentCpuTime;
        _lastTimestamp = currentTimestamp;

        if (elapsedWallSeconds <= 0)
            return new CpuSnapshot();

        //
        // Actual cores consumed by this process.
        //
        var usedCores = cpuTimeSeconds / elapsedWallSeconds;

        //
        // Traditional process CPU %
        // (100% = one full core)
        //
        var processCpuPercent =
            usedCores / Environment.ProcessorCount * 100.0;

        //
        // Container-relative CPU %
        // (matches Azure Container Apps metrics)
        //
        var containerCpuPercent =
            usedCores / _containerCpuQuota * 100.0;

        return new CpuSnapshot
        {
            UsedCores = Math.Round(usedCores, 3),
            ProcessCpuPercent = Math.Round(processCpuPercent, 2),
            ContainerCpuPercent = Math.Round(containerCpuPercent, 2),
            ContainerCpuQuota = _containerCpuQuota
        };
    }

    private static double GetContainerCpuQuota()
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return Environment.ProcessorCount;

            const string cpuMaxPath = "/sys/fs/cgroup/cpu.max";

            if (!File.Exists(cpuMaxPath))
                return Environment.ProcessorCount;

            var parts = File.ReadAllText(cpuMaxPath)
                .Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2)
                return Environment.ProcessorCount;

            if (parts[0] == "max")
                return Environment.ProcessorCount;

            var quota = double.Parse(parts[0]);
            var period = double.Parse(parts[1]);

            return quota / period;
        }
        catch
        {
            return Environment.ProcessorCount;
        }
    }
}

public sealed class CpuSnapshot
{
    public double UsedCores { get; set; }

    public double ProcessCpuPercent { get; set; }

    public double ContainerCpuPercent { get; set; }

    public double ContainerCpuQuota { get; set; }
}