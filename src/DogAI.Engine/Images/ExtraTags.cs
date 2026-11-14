using DogAI.Core.Images;

namespace DogAI.Engine.Images;

/// <summary>
/// The full tag list the owner may add in Models › On this PC › Pictures › Tags (off by default; the owner's decision,
/// 2026-11-14). DOG.ai's bundled list leaves some tags out; this file brings back every tag the build rules keep
/// (<see cref="TagCsv"/>), merged into <see cref="TagList.Current"/> in memory. The file lives in
/// <c>&lt;models&gt;\image\tags</c> under a fixed name, so no path is saved anywhere and a portable move needs nothing.
/// Its presence is the switch: at start, and when it is added, it is read; Remove deletes it.
/// </summary>
public sealed class ExtraTags
{
    /// <summary>The file inside the image models folder.</summary>
    public const string RelativePath = "tags/full-tag-list.csv";

    /// <summary>The use case its download carries, so it never becomes a chat-model record.</summary>
    public const string UseCase = "image-tags";

    /// <summary>The pinned source: <c>tags/danbooru_e621_merged.csv</c> of a1111-sd-webui-tagcomplete at commit 40ad070a02 (MIT licence).</summary>
    public const string SourceRepo = "DominikDoom/a1111-sd-webui-tagcomplete";
    public const string SourceCommit = "40ad070a02";
    public const string SourceFile = "danbooru_e621_merged.csv";
    public const string SourceUrl = "https://github.com/DominikDoom/a1111-sd-webui-tagcomplete/raw/40ad070a02/tags/danbooru_e621_merged.csv";
    public const long SourceBytes = 6_024_803;
    public const string SourceSha256 = "aa9eab8435562f86d7ac0d81f2f7bedcba9f6b93a806010708702406bba85e18";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>True while the full list is in use.</summary>
    public bool IsOn { get; private set; }

    /// <summary>How many tags the full list adds to the bundled one.</summary>
    public int Count { get; private set; }

    /// <summary>Why the file in the tags folder could not be used, in words for the owner; null when all is well.</summary>
    public string? Problem { get; private set; }

    /// <summary>The image models folder last looked in; null before the first look.</summary>
    public string? Root { get; private set; }

    /// <summary>Raised when the state changes. May come from a background thread.</summary>
    public event Action? Changed;

    public static string PathIn(string imageRoot) => Path.Combine(imageRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Reads the file if it is there (at start, after a download, or when the models folder changed).</summary>
    /// <summary>
    /// Reads the list again when the image folder is another one than last time (the owner chose another models folder).
    /// Called where tags are checked, so the tag check never follows a folder that is no longer in use.
    /// </summary>
    public Task FollowAsync(string? imageRoot) =>
        string.Equals(imageRoot, Root, StringComparison.OrdinalIgnoreCase) ? Task.CompletedTask : LoadAsync(imageRoot);

    public async Task LoadAsync(string? imageRoot)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Root = imageRoot;
            var path = imageRoot is null ? null : PathIn(imageRoot);
            if (path is null || !File.Exists(path))
            {
                Set(null, null);
                return;
            }
            try
            {
                var rows = await Task.Run(() => TagCsv.ReadFile(path)).ConfigureAwait(false);
                Set(rows, null);
            }
            catch (Exception e) when (e is TagFileException or IOException or UnauthorizedAccessException)
            {
                Set(null, $"The tag file in the image models folder could not be used. {Message(e)} Remove it and add it again.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds a tag file the owner chose: it is read first (a file that is not a tag list is refused with a plain message,
    /// and nothing is copied), then copied into the tags folder and put into use.
    /// </summary>
    public async Task AddAsync(string imageRoot, string source)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var target = PathIn(imageRoot);
            var temp = target + ".adding";
            IReadOnlyList<TagRow> rows;
            try
            {
                rows = await Task.Run(() =>
                {
                    // Checked where it is first, so a file that is refused leaves nothing behind. Then the copy itself is
                    // read: what is kept is exactly what was checked, also when the chosen file changes meanwhile.
                    TagCsv.ReadFile(source);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, temp, overwrite: true);
                    try
                    {
                        var kept = TagCsv.ReadFile(temp);
                        File.Move(temp, target, overwrite: true);
                        return kept;
                    }
                    catch
                    {
                        File.Delete(temp);
                        throw;
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new TagFileException("The file could not be read: " + e.Message);
            }
            Root = imageRoot;
            Set(rows, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes the file and goes back to the bundled list.</summary>
    public async Task RemoveAsync(string? imageRoot)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Root = imageRoot;
            if (imageRoot is not null)
            {
                var path = PathIn(imageRoot);
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".adding")) File.Delete(path + ".adding");
            }
            Set(null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Set(IReadOnlyList<TagRow>? rows, string? problem)
    {
        if (rows is null)
        {
            TagList.UseExtra(null);
            (IsOn, Count) = (false, 0);
        }
        else
        {
            var merged = TagList.Bundled.With(rows);
            TagList.UseExtra(merged);
            (IsOn, Count) = (true, Math.Max(0, merged.Count - TagList.Bundled.Count));
        }
        Problem = problem;
        Changed?.Invoke();
    }

    private static string Message(Exception e) => e is TagFileException ? e.Message : "It could not be read: " + e.Message;
}
