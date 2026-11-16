namespace DogAI.Core.Models;

/// <summary>
/// Wording for a chat model's picture input in Models › On this PC › Chat models (M17 step 3): Get picture input, Remove
/// picture input, what Delete does with a projector, and the lines on a model's row. The rules are elsewhere:
/// <see cref="Projectors"/> (Core) and the engine's <c>ModelProjector</c>.
/// </summary>
public static class PictureInput
{
    /// <summary>The download list's title for a projector fetched on its own.</summary>
    public static string JobTitle(string model) => $"Picture input for {model}";

    /// <summary>Get picture input while DOG.ai is offline.</summary>
    public static string GoOnline(string repo) =>
        $"DOG.ai looks for the file in {repo}, where this model came from. That needs DOG.ai to go online (to Hugging Face only) while you are in Models.";

    /// <summary>Get picture input's offer, with everything the owner is agreeing to.</summary>
    public static string Offer(string file, long bytes, string repo, string folder, string model) =>
        $"{file} ({Sizes.Gb(bytes)}) from {repo}. It goes into {folder}, beside the model, and {model} can then see pictures you put in a chat.";

    public const string OfferMore =
        "It is loaded with the model whenever it is there and takes about 1.1 GB more graphics memory. Remove picture input takes it away again.";

    public static string NoneIn(string repo) => $"{repo} has no picture input file for this model, so it cannot see pictures.";

    public static string NameTaken(string file, string folder) =>
        $"A file named {file} is already in {folder}, and it is not this model's picture input. Move it elsewhere first, then try again.";

    public static string NotThisModel(string file, string model) =>
        $"{file} in that repository is made for another model, so {model} could not use it. Nothing was downloaded.";

    public static string Started(string file, string model) => $"Downloading {file} for {model}. It can see pictures once the download is done.";

    public static string Arrived(string model) => $"{model} can see pictures now. It is loaded with its picture input from now on.";

    /// <summary>The row's line while its projector downloads.</summary>
    public static string Getting(string status) => $"Getting picture input: {status}";

    /// <summary>Remove picture input's question: which files go, from where, and that it is for good.</summary>
    public static string RemoveText(string model, IReadOnlyList<(string Name, long Bytes)> files, string folder) =>
        $"This permanently deletes {List(files)} from {folder}. It does not go to the Recycle Bin. " +
        $"{model} then loads without picture input and cannot see pictures.";

    public const string RemoveMore =
        "It frees about 1.1 GB of graphics memory while the model is loaded. Get picture input downloads the file again.";

    /// <summary>Said when the projectors removed were also another model's in the same folder.</summary>
    public static string AlsoLoses(IReadOnlyList<string> models) =>
        $"{Join(models)} in the same folder {(models.Count == 1 ? "uses" : "use")} the same file and cannot see pictures afterwards either.";

    public static string Removed(string model, IReadOnlyList<string> alsoLost) =>
        $"Removed the picture input of {model}" + (alsoLost.Count > 0 ? $" (and of {Join(alsoLost)})." : ".");

    /// <summary>Delete's line when the projector goes with the model.</summary>
    public static string DeletedWith(IReadOnlyList<(string Name, long Bytes)> files) =>
        $"Its picture input, {List(files)}, is deleted with it: no other model in the folder uses it.";

    /// <summary>Delete's line when another model in the folder still uses the projector.</summary>
    public static string KeptOnDelete(IReadOnlyList<string> files, IReadOnlyList<string> users) =>
        $"Its picture input ({Join(files)}) stays: {Join(users)} in the same folder {(users.Count == 1 ? "uses" : "use")} it too.";

    /// <summary>
    /// Whether to say that a benchmark's layer setting is not used now (<see cref="LoadDecision.Decide"/> uses it only
    /// for a load with the projector as it was measured). Not said when the benchmark put every layer on the card and the
    /// projector has only gone since: the model then has more room than it had, and loads whole as before.
    /// </summary>
    public static bool BenchmarkUnused(ModelRecord? record, bool seesNow) =>
        record is { TunedGpuLayers: { } layers } && record.TunedWithProjector != seesNow && (layers >= 0 || seesNow);

    /// <summary>Under a benchmark's figures whose layer setting was found with the projector loaded differently from now.</summary>
    public static string BenchmarkStale(bool seesNow) => seesNow
        ? "Its layer setting was found without picture input, so it is not used now. Run the benchmark again to find one with it."
        : "Its layer setting was found with picture input, so it is not used now. Run the benchmark again to find one without it.";

    /// <summary>The defaults block's note when the General default has no projector.</summary>
    public static string GeneralCannotSee(string model) =>
        $"{model}, the General default, cannot see pictures. Models marked “{Projectors.Label}” can; for one downloaded in DOG.ai, " +
        "⋯ › Get picture input looks for its file.";

    private static string List(IReadOnlyList<(string Name, long Bytes)> files) =>
        Join(files.Select(f => $"{f.Name} ({Sizes.Gb(f.Bytes)})").ToList());

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
