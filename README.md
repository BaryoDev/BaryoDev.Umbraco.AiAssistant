# BaryoDev.Umbraco.AiAssistant

Claude-powered writing help for the Umbraco 17 backoffice. Free, bring your own key: you use your own Anthropic account and pay Anthropic directly for usage. Every result is a suggestion. Nothing is written to your content without an editor accepting it.

Targets Umbraco 17 and .NET 10. The server calls the Claude Messages API (`https://api.anthropic.com/v1/messages`). The browser never talks to Anthropic and never sees the key.

## What it does

- **Polish.** An action on text and rich text properties. The suggestion shows beside the original. The editor accepts, edits or discards it.
- **SEO pack.** A title under 60 characters, a meta description of at most 154 characters and a share text, for a page. Property aliases are configurable. Only text and rich text properties are sent as source, even if `SourceProperties` names a block list or grid property.
- **Alt text.** One plain sentence for Image media whose alt property is empty. Available on the media item and as a bulk job. SVG and unreadable files are skipped with a reason. Existing alt text is never replaced unless you ask.
- **Translate.** Translates the default-language text of culture-variant content, and the page name, into another language from your language list. Bulk job only. The page name appears as an item field with the alias `$name` and is saved as the page name of that language. A culture that already has text is left alone unless you ask.
- **Bulk review queue.** A dashboard in the Content section. Pick a node (with or without descendants) or a document type, pick a job, see the estimated cost, confirm, start. Jobs are stored in the database, run one at a time, survive a restart, can be cancelled, and retry 429 and 5xx responses with backoff (honouring `retry-after`). Each result shows original and suggestion side by side with the facts check. The editor accepts, edits or rejects each one, or accepts all that passed the check. Accept saves a draft. Publishing is a separate button.
- **Brand voice.** Edit the writing rules in the Settings dashboard. "Draft from my articles" reads up to 10 chosen pages and proposes rules, which you review before saving.
- **Key, usage and cap.** A Settings dashboard to store the API key, pick the model, see this month's tokens and estimated cost, and set a monthly spend cap.

## Install

```
dotnet add package BaryoDev.Umbraco.AiAssistant
```

Add your Anthropic API key in the backoffice. Go to Settings, open the "AI Assistant" dashboard, paste your key and click Test, then Save. Get a key at https://platform.claude.com/. No restart is needed.

## Key

Two ways, and configuration wins when both are set:

1. The Settings dashboard. The key is write-only: no endpoint returns it, it is never logged, and a failed Test gives a plain reason without repeating it. It is stored encrypted in the Umbraco database with ASP.NET Data Protection. The key ring is its own and lives under `umbraco/Data/DataProtection-Keys/AiAssistant` (change it with `KeysPath`). If the ring is lost, the dashboard says the saved key can no longer be read and asks you to enter it again.
2. Configuration or environment: `BaryoDev__AiAssistant__Key`. Keep it out of appsettings in git.

Only users with Settings section access can read the state or change the key, the cap or the rules. This is enforced on the server.

When the key comes from configuration, saving or removing a key in the dashboard is refused with a 400, the key field is disabled, and Test checks the configured key. The cap, model and rules can still be saved.

## Settings

```json
"BaryoDev": {
  "AiAssistant": {
    "Model": "claude-sonnet-5-5",
    "MaxCharacters": 20000,
    "RequestsPerMinute": 6,
    "MaxBulkItems": 500,
    "MonthlySpendCap": 25,
    "InputPricePerMillion": 3,
    "OutputPricePerMillion": 15,
    "AltTextProperty": "altText",
    "TranslateSourceCulture": "",
    "Seo": { "TitleProperty": "seoTitle", "DescriptionProperty": "seoDescription", "ShareTextProperty": "shareText" },
    "Rules": "",
    "DocumentTypes": [ { "Alias": "post", "Properties": [ "body", "summary" ] } ]
  }
}
```

Anything set in configuration wins over the same setting saved in the dashboard, and the dashboard says so. The prices above are examples, not a quote: prices change, so enter the figures from Anthropic's pricing page.

- `Rules` empty means the built-in rules. The facts rule and the answer format always stay and cannot be removed.
- `DocumentTypes` empty means every text and rich text property.
- `RequestsPerMinute` limits single actions per backoffice user.
- `TranslateSourceCulture` empty means the default language.

## Bulk jobs

- A job lists who started it by display name (`createdBy`). Access is still checked against the user id.
- Each item has a `notes` list for advice such as a length over the limit, separate from the facts check (`missing`).
- A run limited to one language leaves out invariant pages, unless that language is the default language.
- Edit, reject and resume need the same permission on the node as accept. Cancel is open to the user who started the run and to Settings users.
- If one item fails with an unexpected error during "accept all", that item is marked failed with a plain message and the rest continue. Any unexpected error from the API gives a 500 with a plain message, and the detail goes to the server log only.

## Spend cap and usage

Input and output tokens are counted per call from the API response and stored per month (UTC). Cost is an estimate from the prices you enter, taken at the price in force when the call was made. The cap is checked on the server before each call. At the cap, calls stop with a clear message and the API answers 402. The default is no cap. Bulk jobs always show an estimated cost first and need a confirm. The key Test button is counted too. Calls already running when the cap is reached finish, so spend can pass the cap by the cost of those calls. A cap needs a price: with no price set while a cap is active, calls are refused. A cap set in configuration cannot be weakened from the dashboard.

## Safety

- Page text and image bytes are untrusted. They are sent inside separate data blocks and the model is told not to follow instructions in them. No tools are given to the model.
- Model output is only a suggestion. Suggestions containing a script tag, an event-handler attribute or a script link are refused. For rich text, the tags, attributes and links must match the original, otherwise the facts check fails.
- The facts check compares numbers and capitalised names between the source and the suggestion. Translation does not compare names: it checks numbers, quoted terms and web addresses, plus markup. A suggestion that fails is shown to the editor and is never accepted by "accept all".
- A text over its length limit (for example a meta description over 154 characters) is shown as a length note in the item's `notes` list. It is not a failed facts check.
- The SEO facts check can miss a single unknown word in a title, such as an invented name beside words that are on the page. Read titles before accepting.
- Bulk jobs respect each user's access: Content section access to run, and per-node and per-language permissions when accepting or publishing.
- Content must be re-read at accept time. If the content changed after the suggestion was made, accept is refused.

## What is sent to Anthropic

- Polish, SEO pack, translate: the text of the page properties involved, plus short instructions.
- Alt text: the image, and its file name.
- Brand voice drafting: the text of the pages you choose.
- The API key, in the request header.

Nothing is sent until a person starts an action or a bulk job. Review Anthropic's terms and privacy policy for how they handle API data.

## Limits

- Job state and usage are kept in the Umbraco key-value store with in-process locks. Run it on a single server instance.
- Tested by hand on Umbraco 17. Umbraco 16 and 18 are not supported by this version.
- Cost figures are estimates. Check your Anthropic account for the real bill.

## Dependencies

AngleSharp (MIT) parses model output so markup is checked the way a browser reads it. Images are sent as stored, and Claude scales large ones itself. Images over 8000 pixels on an edge or over 4.5 MB are skipped with a reason.

## Tests

`dotnet test` runs the unit tests with a fake Claude client. No key is needed. One live test is skipped unless `BaryoDev__AiAssistant__Key` is set.

## License

MIT. See [LICENSE](LICENSE).
