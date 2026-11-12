using System.Text;
using System.Text.Json.Nodes;
using DogAI.Core.Characters;

namespace DogAI.Tests;

public sealed class CharacterCardTests
{
    private const string V2 = """
        {
          "spec": "chara_card_v2", "spec_version": "2.0",
          "data": {
            "name": "Rex", "description": "A dog.", "personality": "Loyal", "scenario": "A farm",
            "first_mes": "Woof!", "mes_example": "<START>", "creator_notes": "Hi there",
            "system_prompt": "", "post_history_instructions": "Be brief.",
            "alternate_greetings": ["Bark.", "Growl."], "tags": ["dog", "farm"],
            "creator": "Ann", "character_version": "1.2",
            "extensions": { "talkativeness": "0.5", "depth_prompt": { "depth": 4, "prompt": "x" } },
            "character_book": { "entries": [ { "keys": ["bone"], "content": "Rex loves bones." } ] }
          }
        }
        """;

    private const string V3 = """
        {
          "spec": "chara_card_v3", "spec_version": "3.0",
          "data": {
            "name": "Mia", "description": "A cat.", "first_mes": "Meow.", "tags": [],
            "nickname": "Mimi", "group_only_greetings": ["Hello all"], "source": ["somewhere"],
            "extensions": {}, "assets": [ { "type": "icon", "uri": "ccdefault:", "name": "main", "ext": "png" } ]
          }
        }
        """;

    [Fact]
    public void Reads_a_v2_card_and_keeps_the_parts_it_does_not_edit()
    {
        var c = CharacterCard.FromJson(V2);

        Assert.Equal("Rex", c.Name);
        Assert.Equal("A dog.", c.Description);
        Assert.Equal("Woof!", c.FirstMessage);
        Assert.Equal(["Bark.", "Growl."], c.AlternateGreetings);
        Assert.Equal(["dog", "farm"], c.Tags);
        Assert.Equal("Be brief.", c.PostHistoryInstructions);
        Assert.Equal(["Woof!", "Bark.", "Growl."], c.Greetings());
        Assert.Contains("Rex loves bones.", CharacterCard.EmbeddedLorebookJson(c));

        var back = JsonNode.Parse(CharacterCard.ToV2Json(c))!;
        Assert.Equal("chara_card_v2", (string?)back["spec"]);
        Assert.Equal(4, (int?)back["data"]!["extensions"]!["depth_prompt"]!["depth"]);
        Assert.Equal("bone", (string?)back["data"]!["character_book"]!["entries"]![0]!["keys"]![0]);
    }

    [Fact]
    public void Reads_a_v3_card_and_writes_its_extra_fields_back()
    {
        var c = CharacterCard.FromJson(V3);
        Assert.Equal("Mia", c.Name);

        var back = JsonNode.Parse(CharacterCard.ToV3Json(c))!["data"]!;
        Assert.Equal("Mimi", (string?)back["nickname"]);
        Assert.Equal("Hello all", (string?)back["group_only_greetings"]![0]);
        Assert.Equal("ccdefault:", (string?)back["assets"]![0]!["uri"]);
        Assert.NotNull(back["creation_date"]);

        // V2 output stays within the V2 field set.
        var v2 = JsonNode.Parse(CharacterCard.ToV2Json(c))!["data"]!.AsObject();
        Assert.False(v2.ContainsKey("nickname"));
        Assert.True(v2.ContainsKey("extensions"));
    }

    [Fact]
    public void Reads_an_old_v1_card()
    {
        var c = CharacterCard.FromJson("""{ "name": "Old", "description": "d", "personality": "p", "scenario": "s", "first_mes": "f", "mes_example": "e" }""");
        Assert.Equal(("Old", "d", "p", "s", "f", "e"), (c.Name, c.Description, c.Personality, c.Scenario, c.FirstMessage, c.ExampleMessages));
        Assert.Equal("", c.ExtraJson);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "spec": "chara_card_v2", "data": { "description": "no name" } }""")]
    public void Refuses_files_that_are_not_cards(string json) =>
        Assert.Throws<CardFormatException>(() => CharacterCard.Read(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Png_cards_round_trip_and_keep_the_picture()
    {
        var picture = PngText.SolidSquare(8, 10, 20, 30);
        var original = CharacterCard.FromJson(V2);
        original.Image = picture;

        var png = CharacterCard.ToPng(original);
        Assert.True(PngText.IsPng(png));
        var text = PngText.ReadText(png);
        Assert.True(text.ContainsKey("chara"));
        Assert.True(text.ContainsKey("ccv3"));

        var read = CharacterCard.Read(png);
        Assert.Equal(original.Name, read.Name);
        Assert.Equal(original.AlternateGreetings, read.AlternateGreetings);
        Assert.Equal(original.Tags, read.Tags);
        Assert.Contains("Rex loves bones.", CharacterCard.EmbeddedLorebookJson(read));
        Assert.Same(png, read.Image);

        // Exporting again replaces the card text instead of adding a second copy.
        read.Name = "Rex 2";
        var again = CharacterCard.ToPng(read);
        Assert.Equal("Rex 2", CharacterCard.Read(again).Name);
        var raw = Encoding.Latin1.GetString(again);
        Assert.Equal(1, CountOf(raw, "tEXtchara\0"));
        Assert.Equal(1, CountOf(raw, "tEXtccv3\0"));
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }

    [Fact]
    public void A_png_with_both_versions_is_read_from_the_v3_entry()
    {
        var png = PngText.WithText(PngText.SolidSquare(4, 0, 0, 0), new Dictionary<string, string>
        {
            ["chara"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{ "spec": "chara_card_v2", "data": { "name": "Old" } }""")),
            ["ccv3"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{ "spec": "chara_card_v3", "data": { "name": "New" } }""")),
        });
        Assert.Equal("New", CharacterCard.Read(png).Name);
    }

    [Fact]
    public void A_card_without_a_picture_exports_on_a_plain_square()
    {
        var png = CharacterCard.ToPng(new Character { Name = "Blank" });
        Assert.Equal("Blank", CharacterCard.Read(png).Name);
    }

    [Fact]
    public void A_plain_picture_is_not_a_card() =>
        Assert.Throws<CardFormatException>(() => CharacterCard.Read(PngText.SolidSquare(4, 1, 2, 3)));

    [Theory]
    [InlineData("{{char}} meets {{user}}", "Rex meets Sam")]
    [InlineData("{{Char}} and {{USER}}", "Rex and Sam")]
    [InlineData("<BOT> greets <USER>", "Rex greets Sam")]
    public void Names_are_filled_in(string text, string expected) =>
        Assert.Equal(expected, CharacterPrompt.Fill(text, "Rex", "Sam"));

    [Fact]
    public void The_prompt_uses_the_cards_own_instruction_and_can_include_the_default()
    {
        var own = CharacterPrompt.Build(new Character { Name = "R", SystemPrompt = "Custom. {{original}}" }, null);
        Assert.StartsWith("Custom. You are R in an ongoing fictional role-play with User.", own);
        var plain = CharacterPrompt.Build(new Character { Name = "R" }, new Persona { Name = "Sam" });
        Assert.StartsWith("You are R in an ongoing fictional role-play with Sam.", plain);
    }
}
