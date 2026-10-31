using System.Text.Json;
using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Jobs;
using BaryoDev.Umbraco.AiAssistant.Media;
using BaryoDev.Umbraco.AiAssistant.Polish;
using BaryoDev.Umbraco.AiAssistant.Translate;

namespace BaryoDev.Umbraco.AiAssistant.Tests;

public class MarkupTests
{
    private const string Page = "<p>Read the <a href=\"/news/creek\" title=\"Creek\">report</a> on Cebu.</p>";

    [Fact]
    public void A_faithful_translation_of_markup_matches()
    {
        Assert.Empty(HtmlGuard.Problems(Page, "<p>Basahin ang <a href=\"/news/creek\" title=\"Sapa\">ulat</a> tungkol sa Cebu.</p>"));
    }

    [Theory]
    [InlineData("<p>Basahin ang <a href=\"/news/creek\">ulat</a> sa Cebu.</p><script>steal()</script>")]
    [InlineData("<p onclick=\"x()\">Basahin ang <a href=\"/news/creek\">ulat</a> sa Cebu.</p>")]
    [InlineData("<p>Basahin ang <a href=\"javascript:steal()\">ulat</a> sa Cebu.</p>")]
    [InlineData("<p>Basahin ang <a href=\"https://elsewhere.test/\">ulat</a> sa Cebu.</p>")]
    [InlineData("<p>Basahin ang <a href=\"/news/creek\">ulat</a> sa Cebu.</p><img src=\"x\">")]
    public void Added_or_changed_markup_is_a_problem(string output)
    {
        Assert.NotEmpty(HtmlGuard.Problems(Page, output));
    }

    [Fact]
    public void Script_event_handlers_and_script_links_are_unsafe_even_when_the_original_had_them()
    {
        Assert.True(HtmlGuard.IsUnsafe("<SCRIPT>x</SCRIPT>", "<SCRIPT>x</SCRIPT>"));
        Assert.True(HtmlGuard.IsUnsafe("<img src=x onerror=alert(1)>", "<img src=x onerror=alert(1)>"));
        Assert.True(HtmlGuard.IsUnsafe("<a href=\" JaVa&#x09;script:x()\">a</a>", "<a href=\" JaVa&#x09;script:x()\">a</a>"));
        Assert.False(HtmlGuard.IsUnsafe(Page, Page));
    }

    [Theory]
    [InlineData("<p>text<script")]
    [InlineData("<p>text<img src=x onerror=alert(1)")]
    [InlineData("<a title=\">\" onclick=x>go</a>")]
    [InlineData("<a title='>' onmouseover=x>go</a>")]
    [InlineData("<svg/onload=alert(1)>")]
    [InlineData("<svg><animate attributeName=\"href\" to=\"javascript:alert(1)\"/></svg>")]
    [InlineData("<svg><a><set attributeName=\"xlink:href\" to=\"javascript:alert(1)\"/>x</a></svg>")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">x</a>")]
    [InlineData("<a href=\"&#x6A;avascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"&#106;&#97;vascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"java\tscript:alert(1)\">x</a>")]
    [InlineData("<a href=\"java&#10;script:alert(1)\">x</a>")]
    [InlineData("<a href=\"  javascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"\u0001javascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"data:text/html,<b>x</b>\">x</a>")]
    [InlineData("<a href=\"vbscript:x\">x</a>")]
    [InlineData("<!--><script>alert(1)</script>-->")]
    [InlineData("<![CDATA[><script>alert(1)</script>]]>")]
    [InlineData("<iframe src=\"https://other.test/\"></iframe>")]
    [InlineData("<object data=\"x.swf\"></object>")]
    [InlineData("<embed src=\"x.swf\">")]
    [InlineData("<base href=\"https://other.test/\">")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://other.test/\">")]
    [InlineData("<link rel=\"stylesheet\" href=\"https://other.test/a.css\">")]
    [InlineData("<form action=\"https://other.test/\"><button>go</button></form>")]
    [InlineData("<math><mi xlink:href=\"javascript:alert(1)\">x</mi></math>")]
    [InlineData("<p style=\"background:url(x)\">x</p>")]
    [InlineData("<img srcset=\"a.png 1x, javascript:alert(1) 2x\">")]
    public void Bypass_shapes_are_unsafe_when_the_original_is_plain(string output)
    {
        Assert.True(HtmlGuard.IsUnsafe("<p>Plain.</p>", output));
        Assert.NotEmpty(HtmlGuard.Problems("<p>Plain.</p>", output));
    }

    [Fact]
    public void An_element_outside_the_allowlist_passes_only_when_the_identical_one_is_in_the_source()
    {
        const string source = "<p>Watch</p><iframe src=\"https://video.test/embed/1\"></iframe>";
        Assert.Empty(HtmlGuard.Problems(source, "<p>Panoorin</p><iframe src=\"https://video.test/embed/1\"></iframe>"));
        Assert.True(HtmlGuard.IsUnsafe(source, "<p>Panoorin</p><iframe src=\"https://video.test/embed/1\" onload=\"x()\"></iframe>"));
        Assert.NotEmpty(HtmlGuard.Problems(source, "<p>Panoorin</p><iframe src=\"https://other.test/\"></iframe>"));
    }

    [Fact]
    public void Plain_text_fields_fail_on_any_markup()
    {
        Assert.True(HtmlGuard.IsUnsafe("Hello", "Hello <b>there</b>", allowMarkup: false));
        Assert.False(HtmlGuard.IsUnsafe("Hello", "Hello there", allowMarkup: false));
    }

    [Fact]
    public void Allowed_schemes_and_relative_links_pass()
    {
        foreach (string href in new[] { "https://a.test/", "http://a.test/", "mailto:a@b.test", "tel:+6300000", "/x", "#top", "//cdn.test/x", "x/y?z=1:2" })
            Assert.False(HtmlGuard.IsUnsafe("", $"<a href=\"{href}\">x</a>"), href);
        Assert.True(HtmlGuard.IsUnsafe("", "<a href=\"ftp://a.test/\">x</a>"));
    }

    private static async Task<BulkItem> Translate(string source, string answerText, bool rich)
    {
        var handler = new TranslateHandler(
            new TranslateService(new Answer(JsonSerializer.Serialize(new { text = answerText }))),
            Opts.Effective(new Configuration.AiAssistantOptions { Key = "test-NOT-A-REAL-KEY-000" }));
        var item = new BulkItem { NodeId = Guid.NewGuid(), Name = "page", Culture = "fil-PH" };
        item.Fields.Add(new FieldChange { Alias = "body", SourceText = source, Original = "", IsRich = rich });
        await handler.RunAsync(item, new JobOptions("fil-PH", false), TestContext.Current.CancellationToken);
        return item;
    }

    [Fact]
    public async Task Translated_markup_with_an_injected_script_is_refused_and_never_accept_all()
    {
        BulkItem item = await Translate(Page, "<p>Basahin ang <a href=\"/news/creek\">ulat</a> sa Cebu.</p><script>steal()</script>", rich: true);
        Assert.Equal(ItemState.Failed, item.State);
        Assert.Equal(FactsStatus.Failed, item.Facts);
    }

    [Fact]
    public async Task A_changed_link_in_a_translation_fails_the_facts_check()
    {
        BulkItem item = await Translate(Page, "<p>Basahin ang <a href=\"https://elsewhere.test/\">ulat</a> sa Cebu.</p>", rich: true);
        Assert.Equal(FactsStatus.Failed, item.Facts);
        Assert.Contains(item.Missing, m => m.StartsWith("markup changed"));
    }

    [Fact]
    public async Task A_faithful_rich_translation_passes()
    {
        BulkItem item = await Translate(Page, "<p>Basahin ang <a href=\"/news/creek\" title=\"Sapa\">ulat</a> tungkol sa Cebu.</p>", rich: true);
        Assert.Equal(FactsStatus.Passed, item.Facts);
        Assert.Equal(ItemState.Ready, item.State);
    }

    [Fact]
    public async Task Markup_in_a_plain_text_translation_fails()
    {
        BulkItem item = await Translate("Visit Cebu today.", "Bisitahin ang <b>Cebu</b> ngayon.", rich: false);
        Assert.Equal(FactsStatus.Failed, item.Facts);
    }

    [Fact]
    public async Task An_answer_that_is_not_the_expected_json_fails_the_translation()
    {
        var handler = new TranslateHandler(new TranslateService(new Answer("<script>x</script> sure!")), Opts.Effective(new Configuration.AiAssistantOptions { Key = "test-NOT-A-REAL-KEY-000" }));
        var item = new BulkItem { NodeId = Guid.NewGuid(), Name = "page", Culture = "fil-PH" };
        item.Fields.Add(new FieldChange { Alias = "body", SourceText = "Visit Cebu today.", Original = "" });
        await Assert.ThrowsAsync<ClaudeException>(() => handler.RunAsync(item, new JobOptions("fil-PH", false), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Polish_with_an_unparsed_answer_or_new_markup_is_failed_not_ready()
    {
        async Task<BulkItem> Run(string answer, string original, bool rich)
        {
            var polish = new PolishService(new Answer(answer), Opts.Effective(new Configuration.AiAssistantOptions { Key = "test-NOT-A-REAL-KEY-000" }), Microsoft.Extensions.Logging.Abstractions.NullLogger<PolishService>.Instance);
            var item = new BulkItem { NodeId = Guid.NewGuid(), Name = "page" };
            item.Fields.Add(new FieldChange { Alias = "body", Original = original, IsRich = rich });
            await new PolishHandler(polish).RunAsync(item, new JobOptions(null, false), TestContext.Current.CancellationToken);
            return item;
        }

        BulkItem raw = await Run("Here is your text: Kind words.", "Kind words here.", false);
        Assert.Equal(ItemState.Failed, raw.State);

        BulkItem script = await Run(JsonSerializer.Serialize(new { text = "<p>Kind words.</p><script>x()</script>" }), "<p>Kind words here.</p>", true);
        Assert.Equal(ItemState.Failed, script.State);

        BulkItem link = await Run(JsonSerializer.Serialize(new { text = "<p><a href=\"https://elsewhere.test/\">Kind</a> words.</p>" }), "<p><a href=\"/a\">Kind</a> words here.</p>", true);
        Assert.Equal(FactsStatus.Failed, link.Facts);
    }

    [Fact]
    public void Alt_text_with_markup_fails_its_check_and_an_unparsed_answer_throws()
    {
        var item = new BulkItem { NodeId = Guid.NewGuid(), Name = "photo" };
        item.Fields.Add(new FieldChange { Alias = "altText", Original = "", Suggestion = "A dog <script>x()</script>" });
        var handler = new AltTextHandler(new AltTextService(new Answer("x")), new FakeGateway());
        handler.Recheck(item);
        Assert.Equal(FactsStatus.Failed, item.Facts);
        Assert.Throws<ClaudeException>(() => AltTextService.Parse("a dog on a beach"));
    }

    [Fact]
    public void Seo_text_with_markup_fails_its_check()
    {
        var item = new BulkItem { NodeId = Guid.NewGuid(), Name = "page" };
        item.Fields.Add(new FieldChange { Alias = "seoTitle", Role = "title", Original = "", Suggestion = "Creek <img src=x onerror=y>" });
        var handler = new SeoHandler(new Seo.SeoService(new Answer("x")), new FakeGateway());
        handler.Recheck(item);
        Assert.Equal(FactsStatus.Failed, item.Facts);
    }

    private sealed class Answer(string text) : IClaudeClient
    {
        public Task<ClaudeResponse> CompleteAsync(ClaudeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ClaudeResponse(text, new ClaudeUsage(10, 5)));
    }
}
