using BaryoDev.Umbraco.AiAssistant.Seo;
using System.Text.Json;
using BaryoDev.Umbraco.AiAssistant.Configuration;
using BaryoDev.Umbraco.AiAssistant.Polish;
using Microsoft.Extensions.Logging.Abstractions;

namespace BaryoDev.Umbraco.AiAssistant.Tests;

public class FactsCheckTests
{
    [Fact]
    public void A_faithful_rewrite_passes()
    {
        const string suggestion =
            "42 volunteers from the Maligaya Neighbors Club cleaned the creek near Bonifacio Street last Saturday. " +
            "By 10:30 AM they had filled 120 sacks. Thanks go to Kapitan Reyes and the Barangay Council for the trucks.";
        Assert.Empty(FactsCheck.Missing(Sample.Post, suggestion));
    }

    [Fact]
    public void A_dropped_number_is_found()
    {
        string suggestion = Sample.Post.Replace("120 sacks", "many sacks");
        Assert.Contains("120", FactsCheck.Missing(Sample.Post, suggestion));
    }

    [Fact]
    public void A_changed_number_is_found()
    {
        string suggestion = Sample.Post.Replace("42 volunteers", "24 volunteers");
        Assert.Contains("42", FactsCheck.Missing(Sample.Post, suggestion));
    }

    [Fact]
    public void A_dropped_name_is_found()
    {
        string suggestion = Sample.Post.Replace("Kapitan Reyes and the Barangay Council", "local officials");
        IReadOnlyList<string> missing = FactsCheck.Missing(Sample.Post, suggestion);
        Assert.Contains("Reyes", missing);
        Assert.Contains("Kapitan", missing);
        Assert.Contains("Barangay", missing);
    }

    [Fact]
    public void Sentence_starting_words_are_not_treated_as_names()
    {
        Assert.Empty(FactsCheck.Missing("They came early. Everyone helped.", "People came early and everyone helped."));
    }

    [Theory]
    [InlineData("<p>Join us on Sunday.</p><p>Opening hours are long.</p>", "<p>Come along on Sunday.</p><p>The hours are long.</p>")]
    [InlineData("<h2>Needed</h2><p>Edited text here.</p>", "<h2>Required</h2><p>Reworked text here.</p>")]
    [InlineData("<h2>Old Market Fair Ground</h2>", "<h2>The old fairground</h2>")]
    public void Ordinary_capitalised_words_are_not_names(string source, string suggestion)
    {
        Assert.Empty(FactsCheck.Missing(source, suggestion));
    }

    [Fact]
    public void A_word_dropped_from_a_two_word_name_is_found_even_when_it_is_also_an_ordinary_word()
    {
        Assert.Contains("River", FactsCheck.Missing("We love the River Cafe. The river is calm.", "We love the Cafe. The river is calm."));
    }

    [Fact]
    public void An_invented_or_dropped_name_in_the_middle_of_a_sentence_is_still_found()
    {
        Assert.Contains("Reyes", FactsCheck.Missing("<p>Thanks to Kapitan Reyes.</p>", "<p>Thanks to Kapitan Santos.</p>"));
    }

    [Fact]
    public void Html_tags_and_thousands_separators_do_not_cause_false_flags()
    {
        Assert.Empty(FactsCheck.Missing("<p>We raised 12,500 pesos in Santos.</p>", "<p>The group raised 12500 pesos in Santos.</p>"));
    }

    [Fact]
    public async Task The_service_flags_a_suggestion_that_drops_a_fact_and_does_not_hide_it()
    {
        string bad = Sample.Post.Replace("120 sacks", "a lot of sacks");
        var fake = new FakeClaude(_ => JsonSerializer.Serialize(new { text = bad }));
        var service = new PolishService(fake, Opts.Effective(new AiAssistantOptions { Key = "k" }), NullLogger<PolishService>.Instance);

        PolishResult result = await service.PolishAsync(new(Guid.NewGuid(), "post", "body", Sample.Post), CancellationToken.None);

        Assert.True(result.Flagged);
        Assert.Contains("120", result.Missing);
        Assert.Equal(bad, result.Suggestion);
    }

    [Fact]
    public void A_possessive_matches_the_plain_word_in_the_source()
    {
        Assert.Empty(SeoService.Invented("Visit Pinebrook today. The library keeps a lost and found.", "Pinebrook Library's lost and found"));
    }

    [Fact]
    public void A_possessive_of_an_invented_name_is_still_flagged_as_the_name()
    {
        var missing = SeoService.Invented("The market closes early.", "Marcus Webb's stall and Webb\u2019s tent");
        Assert.Contains("Webb", missing);
        Assert.DoesNotContain(missing, m => m.Contains('\''));
    }

    [Fact]
    public void An_invented_name_in_a_title_case_seo_title_is_flagged()
    {
        var missing = SeoService.Invented("The market opens on Saturday.", "Marcus Webb Opens Market");
        Assert.Contains("Webb", missing);
    }

    [Fact]
    public void A_title_case_word_that_shares_a_start_with_a_source_word_passes()
    {
        string source = "The Saturday Market is closing early from October.";
        Assert.Empty(SeoService.Invented(source, "Saturday Market Closes Earlier from October"));
    }

    [Fact]
    public void A_name_that_is_also_a_word_is_still_a_name_before_another_capitalised_word()
    {
        Assert.Contains("Will", FactsCheck.Missing("Will Smith said prices will rise.", "Smith said prices will rise."));
        Assert.Contains("Will", SeoService.Invented("The shop says prices will rise.", "Will Webb says prices will rise."));
        Assert.Empty(FactsCheck.Missing("Prices will rise. Will it rain?", "Prices will rise. Will it rain?"));
    }

    [Fact]
    public void Headings_in_polish_text_are_still_exempt()
    {
        Assert.Empty(FactsCheck.Missing("Saturday Market Closes Earlier\nThe market is open.", "The market is open."));
    }

    [Fact]
    public void A_title_case_verb_passes_but_a_name_beside_another_unknown_word_does_not()
    {
        string library = "Pinebrook Library is now open longer. Weekday hours run to 6 PM.";
        Assert.Empty(SeoService.Invented(library, "Pinebrook Library Extends Weekday Hours to 6 PM"));
        Assert.Contains("Webb", SeoService.Invented("The market opens on Saturday.", "Marcus Webb Opens Market"));
        Assert.Empty(SeoService.Invented("The Saturday Market is closing early from October.", "Saturday Market Closes Earlier from October"));
    }

    [Fact]
    public void The_title_exemption_has_no_holes_for_a_lone_word_an_acronym_or_a_word_used_elsewhere()
    {
        string market = "The Saturday Market opens on Saturday.";
        Assert.Contains("Manila", SeoService.Invented(market, "Market Opens Saturday in Manila"));
        Assert.Contains("FBI", SeoService.Invented(market, "FBI Opens Saturday Market"));
        Assert.Contains("Webb", SeoService.Invented("The Saturday Market is on today.", "Saturday Market Webb Opens Today"));

        string library = "Pinebrook Library is now open longer. Weekday hours run to 6 PM.";
        Assert.Empty(SeoService.Invented(library, "Pinebrook Library Extends Weekday Hours to 6 PM"));
        Assert.Contains("Extends", SeoService.Invented(library, "Pinebrook Library Extends Weekday Hours to 6 PM\nHours were set by Extends Holdings."));
    }
}
