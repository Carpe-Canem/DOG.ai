using System.Text.RegularExpressions;
using DogAI.Core.Models;

namespace DogAI.Tests;

/// <summary>M11: the three groups of the Models section, their addresses, and the names messages use for them.</summary>
public class ModelsPlacesTests
{
    [Fact]
    public void There_are_three_groups_and_every_tab_is_in_one()
    {
        Assert.Equal(["Get models", "On this PC", "Engines"], ModelsPlaces.Groups.Select(g => g.Title));
        var places = ModelsPlaces.Groups.SelectMany(g => g.Places).ToList();
        Assert.Equal(places.Count, places.Select(p => p.Id).Distinct().Count());
        Assert.All(ModelsPlaces.Groups, g => Assert.All(g.Places, p => Assert.Equal(g.Id, p.Group)));
    }

    [Theory]
    [InlineData(null, ModelsPlaces.Recommended)]
    [InlineData("recommended", ModelsPlaces.Recommended)]
    [InlineData("browse", ModelsPlaces.Browse)]
    [InlineData("library", ModelsPlaces.Library)]
    [InlineData("voice", ModelsPlaces.Voice)]
    [InlineData("images", ModelsPlaces.Pictures)]
    [InlineData("IMAGES", ModelsPlaces.Pictures)]
    [InlineData("engine", ModelsPlaces.Engines)]
    [InlineData("downloads", ModelsPlaces.Recommended)]
    [InlineData("no-such-tab", ModelsPlaces.Recommended)]
    public void Addresses_from_before_the_regrouping_still_open_a_place(string? tab, string place) =>
        Assert.Equal(place, ModelsPlaces.Find(tab).Id);

    [Fact]
    public void The_old_tabs_are_in_the_expected_groups()
    {
        Assert.Equal(ModelsPlaces.Get, ModelsPlaces.Find("browse").Group);
        Assert.Equal(ModelsPlaces.OnThisPc, ModelsPlaces.Find("library").Group);
        Assert.Equal(ModelsPlaces.OnThisPc, ModelsPlaces.Find("images").Group);
        Assert.Equal(ModelsPlaces.OnThisPc, ModelsPlaces.Find("voice").Group);
        Assert.Equal(ModelsPlaces.EnginesGroup, ModelsPlaces.Find("engine").Group);
    }

    [Fact]
    public void Addresses_lead_back_to_their_place()
    {
        Assert.Equal("models", ModelsPlaces.Address(ModelsPlaces.Recommended));
        Assert.Equal("models/engines", ModelsPlaces.Address(ModelsPlaces.Engines));
        Assert.Equal("models/pictures", ModelsPlaces.Address(ModelsPlaces.Pictures));
        Assert.Equal("models/pictures", ModelsPlaces.Address(ModelsPlaces.Pictures, ModelsPlaces.Checkpoints));
        Assert.Equal("models/pictures/loras", ModelsPlaces.Address(ModelsPlaces.Pictures, ModelsPlaces.Loras));
        Assert.Equal("models/pictures/tags", ModelsPlaces.Address(ModelsPlaces.Pictures, "TAGS"));
        // A part means nothing outside Pictures.
        Assert.Equal("models/voice", ModelsPlaces.Address(ModelsPlaces.Voice, ModelsPlaces.Tags));
        foreach (var place in ModelsPlaces.Groups.SelectMany(g => g.Places))
        {
            var address = ModelsPlaces.Address(place.Id);
            Assert.Equal(place.Id, ModelsPlaces.Find(address == "models" ? null : address["models/".Length..]).Id);
        }
    }

    [Theory]
    [InlineData(null, ModelsPlaces.Checkpoints)]
    [InlineData("loras", ModelsPlaces.Loras)]
    [InlineData("Tags", ModelsPlaces.Tags)]
    [InlineData("nonsense", ModelsPlaces.Checkpoints)]
    public void A_pictures_part_falls_back_to_checkpoints(string? part, string expected) =>
        Assert.Equal(expected, ModelsPlaces.PicturePart(part));

    [Fact]
    public void The_names_in_messages_match_the_places()
    {
        Assert.Equal(ModelsPlaces.RecommendedName, ModelsPlaces.Name(ModelsPlaces.Recommended));
        Assert.Equal(ModelsPlaces.GetPicturesName, ModelsPlaces.Name(ModelsPlaces.GetPictures));
        Assert.Equal(ModelsPlaces.GetVoiceName, ModelsPlaces.Name(ModelsPlaces.GetVoice));
        Assert.Equal(ModelsPlaces.LibraryName, ModelsPlaces.Name(ModelsPlaces.Library));
        Assert.Equal(ModelsPlaces.PicturesName, ModelsPlaces.Name(ModelsPlaces.Pictures));
        Assert.Equal(ModelsPlaces.LorasName, ModelsPlaces.Name(ModelsPlaces.Pictures, ModelsPlaces.Loras));
        Assert.Equal(ModelsPlaces.TagsName, ModelsPlaces.Name(ModelsPlaces.Pictures, ModelsPlaces.Tags));
        Assert.Equal(ModelsPlaces.VoiceName, ModelsPlaces.Name(ModelsPlaces.Voice));
        Assert.Equal(ModelsPlaces.EnginesName, ModelsPlaces.Name(ModelsPlaces.Engines));
    }

    /// <summary>
    /// No screen or message still sends the owner to a tab that is gone: the old names ("Models › Images",
    /// "Models → Engine") and links to the old addresses must not come back.
    /// </summary>
    [Fact]
    public void Nothing_in_the_source_names_a_place_from_before_the_regrouping()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DogAI.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var old = new Regex("Models (›|→) (Images|Engine\\b(?!s)|Library|Voice|Downloads|Recommended|Browse)|href=\"models/(images|engine|downloads)\"");
        var known = ModelsPlaces.Groups.SelectMany(g => g.Places).Select(p => p.Id).ToHashSet();
        var link = new Regex("href=\"models/([a-z-]+)");
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "src"), "*", SearchOption.AllDirectories))
        {
            if (Path.GetExtension(file) is not (".cs" or ".razor")) continue;
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            var text = File.ReadAllText(file);
            if (old.Match(text) is { Success: true } hit) problems.Add($"{Path.GetFileName(file)}: {hit.Value}");
            foreach (Match m in link.Matches(text))
                if (!known.Contains(m.Groups[1].Value)) problems.Add($"{Path.GetFileName(file)}: {m.Value}");
        }
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
