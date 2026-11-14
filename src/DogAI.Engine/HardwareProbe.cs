using System.Diagnostics;
using System.Runtime.InteropServices;
using DogAI.Core.Models;

namespace DogAI.Engine;

/// <summary>
/// Measures this PC: graphics cards (through nvidia-smi, which comes with the NVIDIA driver),
/// memory, processor and free disk space. Nothing here touches the network.
/// </summary>
public sealed class HardwareProbe
{
    private static readonly TimeSpan SmiTimeout = TimeSpan.FromSeconds(5);
    private string? _smiPath;
    private bool _smiSearched;

    public async Task<HardwareProfile> ProbeAsync(CancellationToken ct = default)
    {
        var gpus = NvidiaSmi.ParseGpus(await RunSmiAsync(NvidiaSmi.GpuQuery, ct).ConfigureAwait(false) ?? "");
        var banner = gpus.Count > 0 ? await RunSmiAsync("", ct).ConfigureAwait(false) : null;
        var (ramTotal, ramFree) = Memory();
        return new HardwareProfile
        {
            Gpus = gpus,
            DriverCuda = banner is null ? null : NvidiaSmi.ParseCudaVersion(banner),
            CpuName = CpuName(),
            CpuThreads = Environment.ProcessorCount,
            RamTotalBytes = ramTotal,
            RamFreeBytes = ramFree,
            Drives = Drives(),
        };
    }

    /// <summary>
    /// Graphics memory one process holds, e.g. the model engine. 0 when unknown.
    /// On Windows nvidia-smi usually prints "[N/A]" per program (the driver runs in WDDM mode),
    /// so Windows' own "GPU Process Memory" counters are read instead.
    /// </summary>
    public async Task<long> VramOfProcessAsync(int processId, CancellationToken ct = default)
    {
        var output = await RunSmiAsync(NvidiaSmi.AppsQuery, ct).ConfigureAwait(false);
        var bytes = output is null ? 0 : NvidiaSmi.ParseApps(output).GetValueOrDefault(processId);
        return bytes > 0 ? bytes : await Task.Run(() => DedicatedVramOf(processId), ct).ConfigureAwait(false);
    }

    private static long DedicatedVramOf(int processId)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            var data = new PerformanceCounterCategory("GPU Process Memory").ReadCategory()["Dedicated Usage"];
            if (data is null) return 0;
            long total = 0;
            foreach (InstanceData sample in data.Values)
                if (NvidiaSmi.CounterPid(sample.InstanceName) == processId) total += sample.RawValue;
            return total;
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>Total and free memory on the main graphics card, for the status bar.</summary>
    public async Task<GpuInfo?> MainGpuAsync(CancellationToken ct = default)
    {
        var output = await RunSmiAsync(NvidiaSmi.GpuQuery, ct).ConfigureAwait(false);
        return output is null ? null : NvidiaSmi.ParseGpus(output).OrderByDescending(g => g.TotalBytes).FirstOrDefault();
    }

    private async Task<string?> RunSmiAsync(string arguments, CancellationToken ct)
    {
        var exe = FindSmi();
        if (exe is null) return null;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.Start();
            var read = process.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SmiTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 ? await read.ConfigureAwait(false) : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private string? FindSmi()
    {
        if (_smiSearched) return _smiPath;
        _smiSearched = true;
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (File.Exists(system)) return _smiPath = system;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), "nvidia-smi.exe");
                if (File.Exists(candidate)) return _smiPath = candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    private static IReadOnlyList<DriveSpace> Drives()
    {
        var drives = new List<DriveSpace>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d is { DriveType: DriveType.Fixed, IsReady: true })
                        drives.Add(new DriveSpace(d.Name.TrimEnd('\\'), d.AvailableFreeSpace, d.TotalSize));
                }
                catch (IOException)
                {
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        return drives;
    }

    private static string? CpuName()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static (long Total, long Free) Memory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref status)
            ? ((long)status.TotalPhys, (long)status.AvailPhys)
            : (0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
