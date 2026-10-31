using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace BaryoDev.Umbraco.AiAssistant.Polish;

/// <summary>
/// Model output is untrusted, because the page text it works from can carry instructions. Both texts are parsed with a
/// real HTML5 parser, so what is checked is what a browser would build. The suggestion has to match the original
/// element for element, attribute for attribute and link for link. Elements, attributes and link schemes outside a
/// short allowlist are refused unless the original has the identical one. Script, event handlers and script links are
/// refused even then. Nothing is rewritten: a suggestion that differs is flagged, an unsafe one is refused.
/// </summary>
public static class HtmlGuard
{
    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    {
        "p", "br", "hr", "wbr", "a", "strong", "b", "em", "i", "u", "s", "sub", "sup", "small", "mark", "abbr", "cite", "q", "del", "ins", "time",
        "span", "div", "section", "article", "aside", "header", "footer", "nav", "main", "address",
        "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "code", "ul", "ol", "li", "dl", "dt", "dd",
        "img", "figure", "figcaption", "table", "caption", "colgroup", "col", "thead", "tbody", "tfoot", "tr", "td", "th",
    };

    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    {
        "href", "src", "alt", "title", "class", "id", "lang", "dir", "colspan", "rowspan", "width", "height", "target", "rel", "datetime", "start",
    };

    private static readonly HashSet<string> UrlAttributes = new(StringComparer.Ordinal)
    {
        "href", "src", "action", "formaction", "xlink:href", "srcset", "poster", "data", "cite", "background", "ping", "longdesc", "manifest", "codebase", "usemap", "icon",
    };

    private static readonly HashSet<string> HardElements = new(StringComparer.Ordinal)
    {
        "script", "style", "base", "meta", "link", "template", "noscript",
    };

    private static readonly string[] SafeSchemes = ["http", "https", "mailto", "tel"];
    private static readonly string[] NeverSchemes = ["javascript", "vbscript", "data"];

    /// <summary>Why the suggestion's markup differs from the original or is not allowed, empty when it is fine.</summary>
    public static IReadOnlyList<string> Problems(string original, string suggestion)
    {
        var problems = new List<string>(Violations(original, suggestion, allowMarkup: true));
        try
        {
            if (!Shape(original).SequenceEqual(Shape(suggestion), StringComparer.Ordinal))
                problems.Add("markup changed: the tags, attributes or links differ from the original");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            problems.Add("the markup could not be checked");
        }
        return problems;
    }

    /// <summary>True when the text breaks the allowlist rules. Used before anything is saved.</summary>
    public static bool IsUnsafe(string? source, string? text, bool allowMarkup = true) =>
        !string.IsNullOrEmpty(text) && Violations(source ?? "", text, allowMarkup).Count > 0;

    public static IReadOnlyList<string> Violations(string source, string text, bool allowMarkup)
    {
        var reasons = new List<string>();
        void Add(string r) { if (!reasons.Contains(r)) reasons.Add(r); }
        try
        {
            var known = Known(source);
            var parsed = Parse(text);
            if (OpenTags(string.Concat(parsed.Select(n => n.ToHtml()))) < OpenTags(text))
                Add("the markup could not be checked: part of it was not a complete tag");
            foreach (IElement e in Walk(parsed))
            {
                string name = Name(e);
                if (!allowMarkup) { Add("markup in a plain text field"); continue; }
                string local = e.LocalName.ToLowerInvariant();
                if (HardElements.Contains(local)) Add($"the {local} element is not allowed");
                else if (!Elements.Contains(name) && !known.Elements.Contains(name)) Add($"the {local} element is not allowed");

                if (local is "animate" or "set" or "animatetransform" or "animatemotion"
                    && e.Attributes.Any(a => a.Name.Equals("attributename", StringComparison.OrdinalIgnoreCase) && a.Value.Contains("href", StringComparison.OrdinalIgnoreCase)))
                    Add("an animation targets a link");

                foreach (IAttr a in e.Attributes)
                {
                    string attr = a.Name.ToLowerInvariant();
                    if (attr.StartsWith("on", StringComparison.Ordinal) || attr == "srcdoc") { Add($"the {attr} attribute is not allowed"); continue; }
                    bool allowedAttr = Attributes.Contains(attr) || attr.StartsWith("data-", StringComparison.Ordinal) || attr.StartsWith("aria-", StringComparison.Ordinal)
                        || known.Attributes.Contains(attr);
                    if (!allowedAttr) Add($"the {attr} attribute is not allowed");

                    if (UrlAttributes.Contains(attr))
                    {
                        foreach (string url in UrlsIn(attr, a.Value))
                        {
                            string? scheme = Scheme(url);
                            if (scheme is null) continue;
                            if (NeverSchemes.Contains(scheme)) Add("an unsafe link was added");
                            else if (!SafeSchemes.Contains(scheme) && !known.Urls.Contains(Clean(url))) Add($"a {scheme}: link is not allowed");
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Add("the markup could not be checked");
        }
        return reasons;
    }

    private static int OpenTags(string html) => System.Text.RegularExpressions.Regex.Count(html, "<[A-Za-z]");

    private sealed record Seen(HashSet<string> Elements, HashSet<string> Attributes, HashSet<string> Urls);

    private static Seen Known(string source)
    {
        var elements = new HashSet<string>(StringComparer.Ordinal);
        var attributes = new HashSet<string>(StringComparer.Ordinal);
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (IElement e in Walk(Parse(source)))
        {
            elements.Add(Name(e));
            foreach (IAttr a in e.Attributes)
            {
                string attr = a.Name.ToLowerInvariant();
                if (attr.StartsWith("on", StringComparison.Ordinal) || attr == "srcdoc") continue;
                attributes.Add(attr);
                if (UrlAttributes.Contains(attr)) foreach (string u in UrlsIn(attr, a.Value)) urls.Add(Clean(u));
            }
        }
        return new Seen(elements, attributes, urls);
    }

    private static List<string> Shape(string html)
    {
        var list = new List<string>();
        ShapeOf(Parse(html), list);
        return list;
    }

    private static string Name(IElement e)
    {
        string local = e.LocalName.ToLowerInvariant();
        string? ns = e.NamespaceUri;
        if (ns is null or "http://www.w3.org/1999/xhtml") return local;
        return local is "svg" or "math" ? local : (ns.Contains("svg", StringComparison.OrdinalIgnoreCase) ? "svg:" : "foreign:") + local;
    }

    private static INodeList Parse(string html)
    {
        var parser = new HtmlParser();
        using var doc = parser.ParseDocument("<!DOCTYPE html><html><head></head><body></body></html>");
        return parser.ParseFragment(html, doc.Body!);
    }

    private static IEnumerable<IElement> Walk(IEnumerable<INode> nodes)
    {
        foreach (INode n in nodes)
        {
            if (n is not IElement e) continue;
            yield return e;
            foreach (IElement c in Walk(e.ChildNodes)) yield return c;
        }
    }

    private static void ShapeOf(IEnumerable<INode> nodes, List<string> into)
    {
        foreach (INode n in nodes)
        {
            if (n is IElement e)
            {
                var parts = e.Attributes.Select(a =>
                {
                    string attr = a.Name.ToLowerInvariant();
                    return UrlAttributes.Contains(attr) ? $"{attr}={string.Join("|", UrlsIn(attr, a.Value).Select(Clean))}" : attr;
                });
                into.Add("<" + Name(e) + " " + string.Join(" ", parts));
                ShapeOf(e.ChildNodes, into);
                into.Add(">");
            }
            else if (n is IComment) into.Add("<!--");
            else if (n is not IText) into.Add("?" + n.NodeType);
        }
    }

    private static IEnumerable<string> UrlsIn(string attr, string value) =>
        attr == "srcset"
            ? value.Split(',').Select(c => c.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
            : [value];

    /// <summary>How a browser reads a URL: leading and trailing control characters and spaces go, tabs and newlines inside go.</summary>
    private static string Clean(string url)
    {
        string trimmed = url.Trim(Enumerable.Range(0, 0x21).Select(i => (char)i).ToArray());
        return new string(trimmed.Where(c => c is not ('\t' or '\n' or '\r')).ToArray());
    }

    private static string? Scheme(string url)
    {
        string v = Clean(url);
        int colon = v.IndexOf(':');
        if (colon <= 0) return null;
        string candidate = v[..colon];
        if (!char.IsAsciiLetter(candidate[0]) || !candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')) return null;
        return candidate.ToLowerInvariant();
    }
}
