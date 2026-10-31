using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Polish;

namespace BaryoDev.Umbraco.AiAssistant.Jobs;

/// <summary>The Claude side of one job kind. Planning and saving are in the gateway.</summary>
public interface IKindHandler
{
    JobKind Kind { get; }

    /// <summary>Fills Suggestion on each field and sets Facts. Throws ClaudeException or ArgumentException.</summary>
    Task RunAsync(BulkItem item, JobOptions options, CancellationToken cancellationToken);

    /// <summary>Runs the facts check again after an editor changed a suggestion.</summary>
    void Recheck(BulkItem item);

    /// <summary>Rough token counts for the cost estimate.</summary>
    (long Input, long Output) Estimate(BulkItem item);
}

public sealed class PolishHandler(PolishService polish) : IKindHandler
{
    public JobKind Kind => JobKind.Polish;

    public async Task RunAsync(BulkItem item, JobOptions options, CancellationToken cancellationToken)
    {
        bool failed = false, unparsed = false;
        var missing = new List<string>();
        foreach (FieldChange f in item.Fields)
        {
            PolishResult r = await polish.PolishAsync(new PolishRequest(item.NodeId, "", f.Alias, f.Original, f.IsRich), cancellationToken);
            f.Suggestion = r.Suggestion;
            failed |= r.Flagged;
            unparsed |= r.Unparsed;
            missing.AddRange(r.Missing);
        }
        item.Missing = missing;
        item.Facts = failed ? FactsStatus.Failed : FactsStatus.Passed;
        if (unparsed)
        {
            item.State = ItemState.Failed;
            item.Reason = "Claude did not answer in the expected form.";
        }
        else if (item.Fields.Any(f => f.Unsafe(f.Suggestion)))
        {
            item.State = ItemState.Failed;
            item.Reason = "The suggestion contains unsafe markup and was refused.";
        }
        else if (item.Fields.All(f => f.Suggestion == f.Original))
        {
            item.State = ItemState.Skipped;
            item.Reason = "Claude suggested no change.";
        }
        else item.State = ItemState.Ready;
    }

    public void Recheck(BulkItem item)
    {
        item.Missing = item.Fields.SelectMany(f => FactsCheck.Missing(f.Original, f.Final ?? "").Concat(HtmlGuard.Problems(f.Original, f.Final ?? ""))).Distinct().ToList();
        item.Facts = item.Missing.Count > 0 ? FactsStatus.Failed : FactsStatus.Passed;
    }

    public (long Input, long Output) Estimate(BulkItem item)
    {
        long chars = item.Fields.Sum(f => (long)f.Original.Length);
        return (chars / 4 + 700, chars / 4 + 100);
    }
}

public sealed class AltTextHandler(Media.AltTextService alt, IContentGateway gateway) : IKindHandler
{
    public JobKind Kind => JobKind.Alt;

    public async Task RunAsync(BulkItem item, JobOptions options, CancellationToken cancellationToken)
    {
        var image = gateway.ReadImage(item.NodeId);
        string text = await alt.DescribeAsync(image, gateway.FileName(item.NodeId), cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            item.State = ItemState.Failed;
            item.Reason = "Claude returned no alt text.";
            return;
        }
        item.Fields[0].Suggestion = text;
        Recheck(item);
        if (item.Facts == FactsStatus.Failed)
        {
            item.State = ItemState.Failed;
            item.Reason = "The alt text contains markup and was refused.";
            return;
        }
        item.State = ItemState.Ready;
    }

    public void Recheck(BulkItem item)
    {
        item.Missing = HtmlGuard.Problems("", item.Fields[0].Final ?? "").ToList();
        item.Facts = item.Missing.Count > 0 ? FactsStatus.Failed : FactsStatus.NotApplicable;
    }

    public (long Input, long Output) Estimate(BulkItem item) => (1800, 80);
}

public sealed class SeoHandler(Seo.SeoService seo, IContentGateway gateway) : IKindHandler
{
    public JobKind Kind => JobKind.Seo;

    public async Task RunAsync(BulkItem item, JobOptions options, CancellationToken cancellationToken)
    {
        string source = gateway.SourceText(item.NodeId, item.Culture) ?? "";
        if (string.IsNullOrWhiteSpace(source))
        {
            item.State = ItemState.Skipped;
            item.Reason = "The page has no text to work from.";
            return;
        }
        Seo.SeoSuggestion s = await seo.SuggestAsync(source, cancellationToken);
        foreach (FieldChange f in item.Fields)
            f.Suggestion = f.Role switch { "title" => s.Title, "description" => s.Description, _ => s.ShareText };
        Check(item, source);
        item.State = ItemState.Ready;
    }

    public void Recheck(BulkItem item) => Check(item, gateway.SourceText(item.NodeId, item.Culture) ?? "");

    private static void Check(BulkItem item, string source)
    {
        string Text(string role) => item.Fields.FirstOrDefault(f => f.Role == role)?.Final ?? "";
        var suggestion = new Seo.SeoSuggestion(Text("title"), Text("description"), Text("share"));
        item.Notes = Seo.SeoService.LengthProblems(suggestion).ToList();
        var problems = Seo.SeoService.FactProblems(source, suggestion);
        item.Missing = problems.Concat(item.Fields.SelectMany(f => HtmlGuard.Problems("", f.Final ?? ""))).Distinct().ToList();
        item.Facts = item.Missing.Count > 0 ? FactsStatus.Failed : FactsStatus.Passed;
    }

    public (long Input, long Output) Estimate(BulkItem item) => (1500, 250);
}

public sealed class TranslateHandler(Translate.TranslateService translate, Settings.IEffectiveSettings settings) : IKindHandler
{
    public JobKind Kind => JobKind.Translate;

    public async Task RunAsync(BulkItem item, JobOptions options, CancellationToken cancellationToken)
    {
        string target = item.Culture ?? options.TargetCulture ?? throw new ArgumentException("No target language.");
        var missing = new List<string>();
        foreach (FieldChange f in item.Fields)
        {
            f.Suggestion = await translate.TranslateAsync(f.SourceText ?? "", settings.Options.TranslateSourceCulture, target, f.IsRich, settings.Options.MaxCharacters, cancellationToken);
            missing.AddRange(Translate.TranslateFacts.Missing(f.SourceText ?? "", f.Suggestion));
        }
        item.Missing = missing.Distinct().ToList();
        item.Facts = item.Missing.Count > 0 ? FactsStatus.Failed : FactsStatus.Passed;
        if (item.Fields.Any(f => f.Unsafe(f.Suggestion)))
        {
            item.State = ItemState.Failed;
            item.Reason = "The translation contains unsafe markup and was refused.";
            return;
        }
        item.State = ItemState.Ready;
    }

    public void Recheck(BulkItem item)
    {
        item.Missing = item.Fields.SelectMany(f => Translate.TranslateFacts.Missing(f.SourceText ?? "", f.Final ?? "")).Distinct().ToList();
        item.Facts = item.Missing.Count > 0 ? FactsStatus.Failed : FactsStatus.Passed;
    }

    public (long Input, long Output) Estimate(BulkItem item)
    {
        long chars = item.Fields.Sum(f => (long)(f.SourceText?.Length ?? 0));
        return (chars / 4 + 600, chars / 3 + 100);
    }
}
