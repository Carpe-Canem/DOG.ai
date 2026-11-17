namespace DogAI.Core.Models;

/// <summary>One of the three groups of the Models section, with the tabs inside it.</summary>
public sealed record ModelsGroup(string Id, string Title, IReadOnlyList<ModelsPlace> Places);

/// <summary>A tab of the Models section: its id (the address is <c>models/&lt;id&gt;</c>), its group and its names.</summary>
public sealed record ModelsPlace(string Id, string Group, string Title, string Subtitle);

/// <summary>
/// Where things are in the Models section (M11): three groups (Get models, On this PC, Engines) in place of the seven
/// tabs there were. The pages, the links and every message that sends the owner somewhere ("Get one in …") take
/// addresses and names from here, so a place is renamed or moved in one spot. Addresses from before the regrouping
/// (<c>models/images</c>, <c>models/engine</c>, <c>models/downloads</c>) still open the right place.
/// </summary>
public static class ModelsPlaces
{
    public const string Get = "get", OnThisPc = "pc", EnginesGroup = "engines";

    public const string Recommended = "recommended", Browse = "browse", GetPictures = "get-pictures", GetVideo = "get-video", GetVoice = "get-voice";
    public const string Library = "library", Pictures = "pictures", Video = "video", Voice = "voice";
    public const string Engines = "engines";

    /// <summary>The address that used to be the Downloads tab; downloads are a button in the Models header now.</summary>
    public const string OldDownloads = "downloads";

    /// <summary>The parts of On this PC › Pictures (<c>models/pictures/&lt;part&gt;</c>).</summary>
    public const string Checkpoints = "checkpoints", Loras = "loras", Tags = "tags";

    public static readonly IReadOnlyList<(string Id, string Title)> PictureParts =
        [(Checkpoints, "Checkpoints"), (Loras, "LoRAs"), (Tags, "Tags")];

    // The places as messages write them, as constants (a message may be one itself). A test keeps each equal to Name(id).
    public const string RecommendedName = "Models › Get models › Recommended";
    public const string GetPicturesName = "Models › Get models › Pictures";
    public const string GetVideoName = "Models › Get models › Video";
    public const string GetVoiceName = "Models › Get models › Voice";
    public const string LibraryName = "Models › On this PC › Chat models";
    public const string PicturesName = "Models › On this PC › Pictures";
    public const string VideoName = "Models › On this PC › Video";
    public const string LorasName = "Models › On this PC › Pictures › LoRAs";
    public const string TagsName = "Models › On this PC › Pictures › Tags";
    public const string VoiceName = "Models › On this PC › Voice";
    public const string EnginesName = "Models › Engines";

    public static readonly IReadOnlyList<ModelsGroup> Groups =
    [
        new(Get, "Get models",
        [
            new(Recommended, Get, "Recommended", "Chat models picked for your PC"),
            new(Browse, Get, "Browse Hugging Face", "Search Hugging Face for GGUF models"),
            new(GetPictures, Get, "Pictures", "Models that draw pictures"),
            new(GetVideo, Get, "Video", "The model that makes short clips"),
            new(GetVoice, Get, "Voice", "Models for listening and speaking"),
        ]),
        new(OnThisPc, "On this PC",
        [
            new(Library, OnThisPc, "Chat models", "Chat and document search models on this PC"),
            new(Pictures, OnThisPc, "Pictures", "Checkpoints and LoRAs on this PC"),
            new(Video, OnThisPc, "Video", "The video model on this PC"),
            new(Voice, OnThisPc, "Voice", "Voice models on this PC"),
        ]),
        new(EnginesGroup, "Engines",
        [
            new(Engines, EnginesGroup, "Engines", "The programs that run chat models and draw pictures"),
        ]),
    ];

    private static readonly Dictionary<string, string> OldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["images"] = Pictures,
        ["engine"] = Engines,
        [OldDownloads] = Recommended,
    };

    /// <summary>The place an address names. Unknown, old and missing names all open something: Recommended at worst.</summary>
    public static ModelsPlace Find(string? tab)
    {
        var id = tab is not null && OldNames.TryGetValue(tab, out var now) ? now : tab;
        return Groups.SelectMany(g => g.Places).FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? Groups[0].Places[0];
    }

    public static ModelsGroup GroupOf(ModelsPlace place) => Groups.First(g => g.Id == place.Group);

    /// <summary>The part of On this PC › Pictures an address names; Checkpoints when it names none.</summary>
    public static string PicturePart(string? part) =>
        PictureParts.Select(p => p.Id).FirstOrDefault(p => p.Equals(part, StringComparison.OrdinalIgnoreCase)) ?? Checkpoints;

    /// <summary>The address of a place, relative to the app, for links.</summary>
    public static string Address(string id, string? part = null)
    {
        var place = Find(id);
        if (place.Id == Recommended) return "models";
        return place.Id == Pictures && part is not null && PicturePart(part) != Checkpoints
            ? $"models/{place.Id}/{PicturePart(part)}"
            : $"models/{place.Id}";
    }

    /// <summary>A place as messages name it: "Models › Get models › Voice", "Models › Engines".</summary>
    public static string Name(string id, string? part = null)
    {
        var place = Find(id);
        var group = GroupOf(place);
        var name = group.Places.Count == 1 ? $"Models › {group.Title}" : $"Models › {group.Title} › {place.Title}";
        return place.Id == Pictures && part is not null
            ? $"{name} › {PictureParts.First(p => p.Id == PicturePart(part)).Title}"
            : name;
    }
}
