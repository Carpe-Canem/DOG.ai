using DogAI.Core.Appearance;
using DogAI.Core.Settings;
using DogAI.Core.Text;

namespace DogAI.Tests;

/// <summary>Spoken lines and actions in roleplay text are marked for colouring; everything else is left as it was.</summary>
public class SpeechMarkupTests
{
    private const string Say = "<span class=\"say\">";

    private static string Html(string markdown, int citations = 0) =>
        string.Concat(MarkdownRenderer.Render(markdown, citations, speech: true).Select(s => s.Html ?? $"[code:{s.Code}]"));

    [Fact]
    public void Quoted_lines_are_marked_and_narration_is_not()
    {
        var html = Html("She looks up. \"You're late,\" she says. \"Sit.\"");
        Assert.Equal($"<p>She looks up. {Say}&quot;You're late,&quot;</span> she says. {Say}&quot;Sit.&quot;</span></p>\n", html);
    }

    [Fact]
    public void Curly_and_angle_quotes_count_and_marks_may_be_mixed()
    {
        Assert.Contains($"{Say}“Hello.”</span>", Html("“Hello.” He waves."));
        Assert.Contains($"{Say}«Bonjour»</span>", Html("«Bonjour» dit-elle."));
        Assert.Contains($"{Say}“Hello.&quot;</span> He", Html("“Hello.\" He waves."));
    }

    [Fact]
    public void Actions_between_asterisks_are_marked_but_emphasis_inside_speech_is_not()
    {
        var html = Html("*taps the map* \"I *really* mean it.\"");
        Assert.Contains("<em class=\"act\">taps the map</em>", html);
        Assert.Contains($"{Say}&quot;I </span><em>{Say}really</span></em>{Say} mean it.&quot;</span>", html);
    }

    [Fact]
    public void Speech_inside_an_action_is_still_speech()
    {
        var html = Html("*She whispers \"go\" and leaves.*");
        Assert.Contains($"<em class=\"act\">She whispers {Say}&quot;go&quot;</span> and leaves.</em>", html);
    }

    [Fact]
    public void An_unfinished_quotation_ends_with_its_paragraph()
    {
        var html = Html("\"This is still being written\n\nNext paragraph.");
        Assert.Contains($"<p>{Say}&quot;This is still being written</span></p>", html);
        Assert.Contains("<p>Next paragraph.</p>", html);
    }

    [Fact]
    public void Inch_marks_and_stray_closing_marks_stay_plain()
    {
        Assert.DoesNotContain(Say, Html("The board is 5\" wide and 12\" long."));
    }

    [Fact]
    public void Code_and_formulas_are_left_alone()
    {
        var html = Html("Type `print(\"hi\")` then $a \"b\" c$.\n\n```\nsay(\"x\")\n```");
        Assert.DoesNotContain(Say, html);
        Assert.Contains("[code:say(\"x\")]", html);
    }

    [Fact]
    public void Every_span_added_is_closed_whatever_the_text()
    {
        foreach (var text in new[] { "\"a *b\" c* \"d", "*\"a* b\" **\"c** d\"", "> \"quote\n> more\" end", "- \"one\n- two\" \"three", "\"[x](https://example.com/a\"b)\"" })
        {
            var html = Html(text);
            Assert.Equal(Count(html, "<span"), Count(html, "</span>"));
            // A span never straddles a tag: nothing but text sits between its start and its end.
            foreach (var piece in html.Split(Say).Skip(1)) Assert.DoesNotContain("<", piece[..piece.IndexOf("</span>", StringComparison.Ordinal)]);
        }
    }

    [Fact]
    public void Raw_html_in_the_text_stays_text()
    {
        var html = Html("\"<b onclick=x>hi</b>\"");
        Assert.DoesNotContain("<b", html);
        Assert.Contains("&lt;b onclick=x&gt;", html);
    }

    [Fact]
    public void Citations_still_work_inside_speech()
    {
        var html = Html("\"It says so [1].\"", citations: 1);
        Assert.Contains("class=\"cite\" data-cite=\"1\"", html);
        Assert.Equal(Count(html, "<span"), Count(html, "</span>"));
    }

    [Fact]
    public void Without_the_switch_nothing_is_marked()
    {
        var html = string.Concat(MarkdownRenderer.Render("*waves* \"Hi.\"").Select(s => s.Html));
        Assert.Equal("<p><em>waves</em> &quot;Hi.&quot;</p>\n", html);
    }

    [Fact]
    public async Task The_setting_is_on_by_default_and_is_saved()
    {
        var store = new MemoryStore();
        var state = new AppearanceState(store, new FixedTheme());
        await state.LoadAsync();
        Assert.True(state.SpeechColours);

        var changes = 0;
        state.Changed += () => changes++;
        await state.SetSpeechColoursAsync(false);
        await state.SetSpeechColoursAsync(false);
        Assert.Equal(1, changes);
        Assert.False(store.Value.SpeechColours);

        var again = new AppearanceState(store, new FixedTheme());
        await again.LoadAsync();
        Assert.False(again.SpeechColours);
    }

    private static int Count(string text, string piece) => text.Split(piece).Length - 1;

    private sealed class MemoryStore : ISettingsStore
    {
        public AppSettings Value { get; set; } = new();
        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(Value);
        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) { Value = settings; return Task.CompletedTask; }
    }

    private sealed class FixedTheme : ISystemTheme
    {
        public bool IsDark => true;
        public event Action? Changed { add { } remove { } }
    }
}
