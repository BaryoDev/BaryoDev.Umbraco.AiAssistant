import { UmbPropertyActionBase } from "@umbraco-cms/backoffice/property-action";
import { UMB_PROPERTY_CONTEXT } from "@umbraco-cms/backoffice/property";
import { UMB_DOCUMENT_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/document";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";

const API = "/umbraco/management/api/v1/ai-assistant";

// Shown on text properties. It does something only on the SEO title, description and share text properties.
export default class SeoPropertyAction extends UmbPropertyActionBase {
  #property;
  #document;
  #auth;

  constructor(host, args) {
    super(host, args);
    this.consumeContext(UMB_PROPERTY_CONTEXT, (c) => (this.#property = c));
    this.consumeContext(UMB_DOCUMENT_WORKSPACE_CONTEXT, (c) => (this.#document = c));
    this.consumeContext(UMB_AUTH_CONTEXT, (c) => (this.#auth = c));
  }

  async execute() {
    if (!this.#document) return;
    const config = this.#auth.getOpenApiConfiguration();
    const token = await config.token();
    const culture = this.#property.getVariantId?.()?.culture ?? null;
    const response = await fetch(API + "/seo", {
      method: "POST",
      credentials: config.credentials,
      headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
      body: JSON.stringify({ nodeId: this.#document.getUnique(), culture }),
    });
    if (!response.ok) {
      let detail = "";
      try { detail = (await response.json()).detail ?? ""; } catch { /* no body */ }
      window.alert(detail || `Request failed (${response.status})`);
      return;
    }
    const r = await response.json();
    const alias = this.#property.getAlias();
    const mine = { [r.titleProperty]: r.title, [r.descriptionProperty]: r.description, [r.shareTextProperty]: r.shareText }[alias];
    if (mine === undefined) {
      window.alert(`Use this on the ${r.titleProperty}, ${r.descriptionProperty} or ${r.shareTextProperty} property.`);
      return;
    }
    const warning = r.problems.length ? `Check before using: ${r.problems.join("; ")}.\n\n` : "";
    const current = await this.#property.getValue();
    const note = current ? `\n\nThis replaces: ${current}` : "";
    const edited = window.prompt(`${warning}Edit if needed, then OK to use it. It is not saved until you save the page.${note}`, mine);
    if (edited !== null) this.#property.setValue(edited);
  }
}

export { SeoPropertyAction as api };
