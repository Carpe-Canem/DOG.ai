using System.Globalization;
using System.Text.RegularExpressions;

namespace DogAI.Core.Models;

public sealed record GpuInfo(string Name, long TotalBytes, long UsedBytes, long FreeBytes);

public sealed record DriveSpace(string Name, long FreeBytes, long TotalBytes);

/// <summary>What this PC has to run models with. Measured by the Engine's hardware probe.</summary>
public sealed record HardwareProfile
{
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public string? CpuName { get; init; }
    public int CpuThreads { get; init; }
    public long RamTotalBytes { get; init; }
    public long RamFreeBytes { get; init; }
    public IReadOnlyList<DriveSpace> Drives { get; init; } = [];

    /// <summary>Highest CUDA version the graphics driver supports, e.g. 13.5. Null without an NVIDIA driver.</summary>
    public Version? DriverCuda { get; init; }

    /// <summary>The card models run on: the one with the most memory.</summary>
    public GpuInfo? MainGpu => Gpus.OrderByDescending(g => g.TotalBytes).FirstOrDefault();

    public static HardwareProfile Unknown { get; } = new();
}

/// <summary>Reads the output of nvidia-smi. Kept here, free of I/O, so it can be tested.</summary>
public static partial class NvidiaSmi
{
    /// <summary>Arguments that make nvidia-smi print one CSV line per card, sizes in MiB.</summary>
    public const string GpuQuery = "--query-gpu=name,memory.total,memory.used,memory.free --format=csv,noheader,nounits";

    /// <summary>Arguments that list the programs using graphics memory, sizes in MiB.</summary>
    public const string AppsQuery = "--query-compute-apps=pid,used_memory --format=csv,noheader,nounits";

    public static IReadOnlyList<GpuInfo> ParseGpus(string output)
    {
        var gpus = new List<GpuInfo>();
        foreach (var line in Lines(output))
        {
            var parts = line.Split(',');
            if (parts.Length < 4) continue;
            if (!TryMiB(parts[^3], out var total) || !TryMiB(parts[^2], out var used) || !TryMiB(parts[^1], out var free)) continue;
            // A card name could in theory contain a comma, so the numbers are read from the end.
            var name = string.Join(',', parts[..^3]).Trim();
            gpus.Add(new GpuInfo(name, total, used, free));
        }
        return gpus;
    }

    /// <summary>Graphics memory in use per process id.</summary>
    public static IReadOnlyDictionary<int, long> ParseApps(string output)
    {
        var apps = new Dictionary<int, long>();
        foreach (var line in Lines(output))
        {
            var parts = line.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out var pid) || !TryMiB(parts[1], out var bytes)) continue;
            apps[pid] = apps.GetValueOrDefault(pid) + bytes;
        }
        return apps;
    }

    [GeneratedRegex(@"CUDA(?: UMD)? Version:\s*(\d+\.\d+)")]
    private static partial Regex CudaVersion();

    /// <summary>Finds the driver's CUDA version in the banner nvidia-smi prints with no arguments.</summary>
    public static Version? ParseCudaVersion(string banner) =>
        CudaVersion().Match(banner) is { Success: true } m && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryMiB(string text, out long bytes)
    {
        var ok = double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mib);
        bytes = ok ? (long)(mib * 1024 * 1024) : 0;
        return ok;
    }
}

public static class Sizes
{
    public const long GiB = 1024L * 1024 * 1024;

    /// <summary>"17.1 GB" style text. Uses binary gigabytes, as Windows does.</summary>
    public static string Gb(long bytes) => bytes >= 100 * GiB
        ? $"{bytes / (double)GiB:0} GB"
        : bytes >= GiB / 10 ? $"{bytes / (double)GiB:0.0} GB" : $"{bytes / 1024d / 1024d:0} MB";
}
