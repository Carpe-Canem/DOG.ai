using DogAI.Core.Chat;
using DogAI.Core.Models;

namespace DogAI.Tests;

/// <summary>
/// Pictures in the chat box (M17 step 4): a JPEG made by the browser control's own encoder passes Core's check once its
/// colour profile is taken out; what the meter says when pictures wait; the wording; and which chat boxes take pictures.
/// </summary>
public sealed class ChatPictureInputTests
{
    /// <summary>
    /// The page writes every picture with the browser control's canvas (attach.js). This file is one it wrote (WebView2,
    /// quality 90): JFIF with a colour profile, which Plain takes out; nothing else may be in it.
    /// </summary>
    [Fact]
    public void A_jpeg_the_browser_wrote_passes_once_plain()
    {
        var made = TestJpegs.Real("canvas-300x200.jpg");
        Assert.NotNull(AttachedPictures.Problem(made)); // the colour profile the encoder adds
        var plain = AttachedPictures.Plain(made);
        Assert.Null(AttachedPictures.Problem(plain));
        Assert.Equal((300, 200), AttachedPictures.SizeOf(plain));
        Assert.True(plain.Length < made.Length);
    }

    [Theory]
    [InlineData(8192, 0, 4 * 1026, PictureRoom.Fits)]          // four pictures in a new chat: 4,104 of 8,192
    [InlineData(8192, 3000, 4 * 1026, PictureRoom.OlderLeftOut)] // with a chat that already holds 3,000
    [InlineData(4096, 0, 4 * 1026, PictureRoom.TooLarge)]       // four do not go in one message at 4,096
    [InlineData(4096, 0, 2 * 1026, PictureRoom.Fits)]
    [InlineData(8192, 7000, 0, PictureRoom.Fits)]               // nothing waits: the meter says the rest
    [InlineData(0, 0, 642, PictureRoom.Fits)]                   // no model loaded
    public void Judge_says_whether_waiting_pictures_fit(int context, int used, int pending, PictureRoom expected) =>
        Assert.Equal(expected, ComposerPictures.Judge(context, used, pending));

    /// <summary>A reply that thought 7,149 tokens filled the room; its thinking is not sent again, so four pictures still fit.</summary>
    [Fact]
    public void The_last_replys_thinking_does_not_count_against_the_pictures()
    {
        var reply = new ChatMessage { Role = ChatRole.Assistant, PromptTokens = 1043, CompletionTokens = 7149,
            ExtraJson = new DogAI.Core.Stories.StoryTurn { Thinking = new ThinkingFacts { Tokens = 7149 } }.ToJson() };
        var next = ComposerPictures.NextPrompt(reply.PromptTokens!.Value, reply.CompletionTokens!.Value, ComposerPictures.ThinkingOf(reply));
        Assert.Equal(1043, next);
        Assert.Equal(PictureRoom.Fits, ComposerPictures.Judge(8192, next, 2729));
        Assert.Equal(PictureRoom.OlderLeftOut, ComposerPictures.Judge(8192, 1043 + 7149, 2729));
        // Without DOG.ai's count, the thinking text stands in at about four characters a token.
        var old = new ChatMessage { Role = ChatRole.Assistant, Reasoning = new string('x', 4000) };
        Assert.Equal(1000, ComposerPictures.ThinkingOf(old));
        Assert.Equal(500, ComposerPictures.NextPrompt(500, 50, 900)); // never less than the prompt
        Assert.Equal(550, ComposerPictures.NextPrompt(500, 50, 0));
    }

    [Fact]
    public void The_room_for_a_message_is_the_context_less_the_reply_reserve()
    {
        Assert.Equal(8192 - 2048, ComposerPictures.MessageRoom(8192));
        Assert.Equal(4096 - 1024, ComposerPictures.MessageRoom(4096));
        Assert.Equal(0, ComposerPictures.MessageRoom(0));
    }

    [Fact]
    public void The_room_lines_name_the_numbers_and_where_to_change_the_size()
    {
        Assert.Null(ComposerPictures.RoomLine(PictureRoom.Fits, 8192, 642));
        var tooLarge = ComposerPictures.RoomLine(PictureRoom.TooLarge, 4096, 4104)!;
        Assert.Contains("4,104", tooLarge);
        Assert.Contains("3,072 of its 4,096", tooLarge);
        Assert.Contains(ModelsPlaces.LibraryName, tooLarge);
        Assert.Contains("oldest messages", ComposerPictures.RoomLine(PictureRoom.OlderLeftOut, 8192, 4104));
        Assert.Equal(" · 642 for the picture", ComposerPictures.MeterPart(642, 1));
        Assert.Equal(" · 4,104 for 4 pictures", ComposerPictures.MeterPart(4104, 4));
        Assert.Equal("", ComposerPictures.MeterPart(0, 0));
    }

    [Fact]
    public void The_cannot_see_lines_point_at_the_mark_and_the_place_where_it_is()
    {
        foreach (var line in new[] { ComposerPictures.CannotSee(true), ComposerPictures.CannotSee(false), ComposerPictures.WaitingUnseen })
        {
            Assert.Contains(Projectors.Label, line.Text);
            Assert.Equal(ModelsPlaces.LibraryName, line.PlaceName);
            Assert.Equal(ModelsPlaces.Address(ModelsPlaces.Library), line.Address);
        }
        Assert.StartsWith("The model loaded cannot see", ComposerPictures.CannotSee(true).Text);
        Assert.StartsWith("Load a model", ComposerPictures.CannotSee(false).Text);
    }

    [Fact]
    public void Counts_are_said_in_the_singular_and_the_plural()
    {
        Assert.Contains("this picture", ComposerPictures.Unseen(1));
        Assert.Contains("the 3 pictures", ComposerPictures.Unseen(3));
        Assert.Contains("1 was left out", ComposerPictures.TooMany(1));
        Assert.Contains("2 were left out", ComposerPictures.TooMany(2));
        Assert.StartsWith("That file", ComposerPictures.Refused(1));
        Assert.StartsWith("2 files", ComposerPictures.Refused(2));
        Assert.Contains("1,024", ComposerPictures.Help);
        Assert.DoesNotContain("svg", ComposerPictures.Accept, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>General, character chats and group chats take pictures; a story's chat box never does (owner's decision).</summary>
    [Fact]
    public void Only_the_three_kinds_of_chat_give_the_box_pictures()
    {
        var pages = Path.Combine(SolutionRoot(), "src", "DogAI.UI", "Pages");
        foreach (var page in new[] { "General.razor", "Characters.razor", "GroupChat.razor" })
            Assert.Contains("OnSendWithPictures=", File.ReadAllText(Path.Combine(pages, page)));
        var withPictures = Directory.EnumerateFiles(Path.Combine(SolutionRoot(), "src", "DogAI.UI"), "*.razor", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("OnSendWithPictures=", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToList();
        Assert.Equal(["Characters.razor", "General.razor", "GroupChat.razor"], withPictures);
    }

    /// <summary>The page opens pictures by their bytes only and never sends anything but the JPEG it made.</summary>
    [Fact]
    public void The_page_script_opens_no_svg_and_writes_only_jpeg()
    {
        var script = File.ReadAllText(Path.Combine(SolutionRoot(), "src", "DogAI.UI", "wwwroot", "js", "attach.js"));
        Assert.DoesNotContain("svg", script.Replace("SVG included", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'image/jpeg'", script);
        Assert.DoesNotContain("image/png'", script);
        Assert.DoesNotContain("fetch(", script);
        Assert.DoesNotContain("localStorage", script);
    }

    private static string SolutionRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DogAI.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}
