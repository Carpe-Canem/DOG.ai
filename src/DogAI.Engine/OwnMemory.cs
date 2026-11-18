using System.Runtime;

namespace DogAI.Engine;

/// <summary>
/// DOG.ai's own memory (the program itself, not the models' programs). Making a clip passes large things through it
/// once a second for minutes: the engine's answers, each preview, then the clip. The .NET runtime collects them only
/// now and then, and what it has collected it keeps for itself rather than giving it back to Windows.
/// <para>
/// Measured over ten Quick clips in a row (M21 step 6), Windows' count of DOG.ai's own memory: it went from 0.2 GB to
/// 1.53 GB. One full collection took it to 1.12 GB (0.42 GB was copies nobody used any more); asked to give back,
/// it fell to 0.39 GB at once (0.72 GB was memory the runtime had collected and kept); 0.30 GB was still in use.
/// With the engine's answers read into one buffer per clip and this asked before and after every clip, the same
/// ten clips averaged 0.31 GB (0.28 GB between clips); asked once more a moment after the page has shown the
/// clip, DOG.ai stayed between 0.23 and 0.29 GB over five clips. So it is asked around every clip: a clip made
/// by a plan needs
/// 12 GB of the PC's memory free (<see cref="DogAI.Core.Video.VideoMemory.PlannedRamNeed"/>), and DOG.ai should not
/// be what holds it.
/// </para>
/// </summary>
public static class OwnMemory
{
    /// <summary>
    /// Collects everything unused, packs what is left together and gives the freed memory back to Windows. Every
    /// thread of DOG.ai waits while it runs (a fraction of a second), so call it only between jobs, never in one.
    /// </summary>
    /// <returns>How long it took.</returns>
    public static TimeSpan GiveBack()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        return clock.Elapsed;
    }
}
