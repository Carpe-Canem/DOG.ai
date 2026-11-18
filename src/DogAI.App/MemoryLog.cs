using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;

namespace DogAI.App;

/// <summary>
/// For checks only (M21 step 6): with <c>DOGAI_TEST_MEMORY_LOG</c> set to a file, DOG.ai writes its own memory there
/// every two seconds, as numbers and nothing else: what Windows counts for the program, and how the .NET runtime
/// splits its part (objects it holds, memory it has taken from Windows, memory inside that which is free). A file
/// beside it named <c>&lt;log&gt;.collect</c> asks for one full collection (what is then left is in use);
/// <c>&lt;log&gt;.giveback</c> asks the runtime to give back what it can. Off unless the variable is set.
/// </summary>
internal sealed class MemoryLog : IDisposable
{
    public const string Variable = "DOGAI_TEST_MEMORY_LOG";

    private readonly string _file;
    private readonly Timer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _busy;

    private MemoryLog(string file)
    {
        _file = file;
        File.WriteAllText(file, "time\tseconds\tnote\tworking_set\tprivate\tobjects\theap\tcommitted\tfree_inside\tgen0\tgen1\tgen2\tlarge\tpinned\tcount0\tcount1\tcount2\n");
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    /// <summary>Starts the log when the variable names a file; null otherwise.</summary>
    public static MemoryLog? StartIfAsked() =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } file ? new MemoryLog(file) : null;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (Asked(".collect"))
            {
                Line("before-collect");
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                Line("after-collect");
            }
            else if (Asked(".giveback"))
            {
                Line("before-giveback");
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                Line("after-giveback");
            }
            else Line("");
        }
        catch (IOException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private bool Asked(string ending)
    {
        var mark = _file + ending;
        if (!File.Exists(mark)) return false;
        File.Delete(mark);
        return true;
    }

    private void Line(string note)
    {
        using var me = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        var generations = info.GenerationInfo.ToArray();
        long Size(int i) => i < generations.Length ? generations[i].SizeAfterBytes : 0;
        var numbers = new[]
        {
            me.WorkingSet64, me.PrivateMemorySize64, GC.GetTotalMemory(false), info.HeapSizeBytes, info.TotalCommittedBytes,
            info.FragmentedBytes, Size(0), Size(1), Size(2), Size(3), Size(4),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
        };
        File.AppendAllText(_file, string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss}\t{_clock.Elapsed.TotalSeconds:0.0}\t{note}\t{string.Join('\t', numbers)}\n"));
    }

    public void Dispose() => _timer.Dispose();
}
