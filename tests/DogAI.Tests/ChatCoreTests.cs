using DogAI.Core.Chat;
using DogAI.Core.Text;

namespace DogAI.Tests;

public class MessageTreeTests
{
    private static ChatMessage Msg(long id, long? parent, ChatRole role = ChatRole.User) =>
        new() { Id = id, ParentId = parent, Role = role, Content = $"m{id}" };

    // 1 ─ 2 ─ 3 ─ 4
    //      └ 5 ─ 6        (5 is a regenerated alternative to 3)
    private static MessageTree Sample() => new(
    [
        Msg(1, null), Msg(2, 1, ChatRole.Assistant), Msg(3, 2), Msg(4, 3, ChatRole.Assistant),
        Msg(5, 2), Msg(6, 5, ChatRole.Assistant),
    ]);

    [Fact]
    public void Path_runs_from_the_first_turn_to_the_chosen_leaf()
    {
        Assert.Equal([1, 2, 3, 4], Sample().PathTo(4).Select(m => m.Id));
        Assert.Equal([1, 2, 5, 6], Sample().PathTo(6).Select(m => m.Id));
        Assert.Empty(Sample().PathTo(null));
        Assert.Empty(Sample().PathTo(99));
    }

    [Fact]
    public void Siblings_are_the_alternatives_at_one_position()
    {
        var tree = Sample();
        Assert.Equal([3, 5], tree.Siblings(tree.Find(5)!).Select(m => m.Id));
        Assert.Single(tree.Siblings(tree.Find(1)!));
    }

    [Fact]
    public void Switching_branch_lands_on_its_newest_end()
    {
        Assert.Equal(4, Sample().LatestLeafUnder(3));
        Assert.Equal(6, Sample().LatestLeafUnder(5));
        Assert.Equal(6, Sample().LatestLeafUnder(1));
    }

    [Fact]
    public void A_missing_saved_leaf_falls_back_to_the_newest_branch()
    {
        Assert.Equal(4, Sample().ResolveLeaf(4));
        Assert.Equal(6, Sample().ResolveLeaf(99));
        Assert.Equal(6, Sample().ResolveLeaf(null));
        Assert.Null(new MessageTree([]).ResolveLeaf(1));
    }

    [Fact]
    public void A_loop_in_bad_data_does_not_hang()
    {
        var tree = new MessageTree([Msg(1, 2), Msg(2, 1)]);
        Assert.Equal(2, tree.PathTo(1).Count);
        tree.LatestLeafUnder(1);
    }
}

public class ContextBudgeterTests
{
    private const int O = ContextBudgeter.PerMessageOverhead;

    private static BudgetItem[] Turns(params int[] tokens) =>
        tokens.Select((t, i) => new BudgetItem(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, t)).ToArray();

    [Fact]
    public void Everything_is_kept_when_it_fits()
    {
        var fit = ContextBudgeter.Fit(1000, 50, Turns(100, 100, 100));
        Assert.Equal(0, fit.Trimmed);
        Assert.Equal(50 + 3 * (100 + O), fit.UsedTokens);
    }

    [Fact]
    public void Oldest_turns_go_first_and_the_instructions_always_stay()
    {
        // Budget fits the instructions plus the newest three messages only.
        var fit = ContextBudgeter.Fit(50 + 3 * (100 + O), 50, Turns(100, 100, 100, 100, 100));
        Assert.Equal(2, fit.FirstIncluded);
        Assert.Equal(50 + 3 * (100 + O), fit.UsedTokens);
    }

    [Fact]
    public void Trimming_never_leaves_a_reply_at_the_front()
    {
        // Four messages would fit, but that would start on an assistant turn, so one more is dropped.
        var fit = ContextBudgeter.Fit(4 * (100 + O), 0, Turns(100, 100, 100, 100, 100));
        Assert.Equal(2, fit.FirstIncluded);
        Assert.Equal(3 * (100 + O), fit.UsedTokens);
    }

    [Fact]
    public void The_newest_message_is_sent_even_if_it_alone_is_too_big()
    {
        var fit = ContextBudgeter.Fit(100, 50, Turns(20, 20, 5000));
        Assert.Equal(2, fit.FirstIncluded);
    }

    [Fact]
    public void Drop_oldest_removes_a_whole_exchange_and_stops_at_the_last_message()
    {
        var turns = Turns(10, 10, 10, 10, 10);
        var fit = ContextBudgeter.DropOldest(new BudgetResult(0, 5 * (10 + O)), turns);
        Assert.Equal(2, fit.FirstIncluded);
        fit = ContextBudgeter.DropOldest(fit, turns);
        Assert.Equal(4, fit.FirstIncluded);
        Assert.Equal(10 + O, fit.UsedTokens);
        Assert.Equal(fit, ContextBudgeter.DropOldest(fit, turns));
    }

    [Theory]
    [InlineData(4096, 1024)]
    [InlineData(8192, 2048)]
    [InlineData(32768, 2048)]
    public void Reply_reserve_is_a_quarter_capped_at_2048(int context, int expected) =>
        Assert.Equal(expected, ContextBudgeter.ReplyReserve(context));
}

public class MarkdownRendererTests
{
    private static string Html(string markdown) =>
        string.Concat(MarkdownRenderer.Render(markdown).Select(s => s.Html ?? $"[code:{s.Language}:{s.Code}]"));

    [Fact]
    public void Model_card_html_tags_are_removed_but_their_text_and_code_stay()
    {
        var text = MarkdownRenderer.StripHtmlTags(
            "<div align=\"center\"><a href=\"x\">Guide</a></div>\n<!-- note -->\n```html\n<b>kept</b>\n```\nA < B and 2<3");
        Assert.Contains("Guide", text);
        Assert.DoesNotContain("<div", text);
        Assert.DoesNotContain("note", text);
        Assert.Contains("<b>kept</b>", text);
        Assert.Contains("A < B and 2<3", text);
    }

    [Fact]
    public void Images_become_links_so_nothing_remote_loads()
    {
        var html = Html("![banner](https://example.com/b.png) and ![](https://example.com/c.png)");
        Assert.DoesNotContain("<img", html);
        Assert.Contains("<a href=\"https://example.com/b.png\">banner</a>", html);
        Assert.Contains(">image</a>", html);
    }

    [Fact]
    public void Renders_ordinary_markdown()
    {
        var html = Html("# Title\n\nSome **bold** text.\n\n| a | b |\n|---|---|\n| 1 | 2 |");
        Assert.Contains("<h1>Title</h1>", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<table>", html);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("hello <img src=x onerror=alert(1)> there")]
    [InlineData("<iframe src=\"https://example.com\"></iframe>")]
    [InlineData("<div onclick=\"x()\">hi</div>")]
    public void Raw_html_from_a_model_is_shown_as_text_not_run(string markdown)
    {
        var html = Html(markdown);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("<div", html);
        Assert.Contains("&lt;", html);
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](file:///C:/Windows/win.ini)")]
    [InlineData("[x](vbscript:msgbox(1))")]
    public void Links_can_only_point_at_web_or_mail_addresses(string markdown)
    {
        var html = Html(markdown);
        Assert.Contains("href=\"#\"", html);
        Assert.DoesNotContain("javascript", html);
        Assert.DoesNotContain("file:", html);
    }

    [Fact]
    public void Web_links_survive() =>
        Assert.Contains("href=\"https://example.com/a\"", Html("[x](https://example.com/a)"));

    [Fact]
    public void Code_blocks_come_out_as_plain_text_segments()
    {
        var segments = MarkdownRenderer.Render("Before\n\n```csharp\nvar x = \"<b>\";\n```\n\nAfter");
        Assert.Equal(3, segments.Count);
        Assert.True(segments[1].IsCode);
        Assert.Equal("csharp", segments[1].Language);
        Assert.Equal("var x = \"<b>\";", segments[1].Code);
        Assert.Contains("After", segments[2].Html);
    }

    [Fact]
    public void An_unfinished_code_block_still_renders_while_streaming()
    {
        var segments = MarkdownRenderer.Render("Here:\n\n```python\nprint(1)");
        Assert.Equal("print(1)", segments[^1].Code);
    }

    [Fact]
    public void Empty_text_renders_nothing() => Assert.Empty(MarkdownRenderer.Render(""));
}
