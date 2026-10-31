import { UmbPropertyActionBase } from "@umbraco-cms/backoffice/property-action";
import { UMB_PROPERTY_CONTEXT } from "@umbraco-cms/backoffice/property";
import { UMB_MEDIA_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/media";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";

const API = "/umbraco/management/api/v1/ai-assistant";

export default class AltTextPropertyAction extends UmbPropertyActionBase {
  #property;
  #media;
  #auth;

  constructor(host, args) {
    super(host, args);
    this.consumeContext(UMB_PROPERTY_CONTEXT, (c) => (this.#property = c));
    this.consumeContext(UMB_MEDIA_WORKSPACE_CONTEXT, (c) => (this.#media = c));
    this.consumeContext(UMB_AUTH_CONTEXT, (c) => (this.#auth = c));
  }

  async execute() {
    if (!this.#media) return;
    const config = this.#auth.getOpenApiConfiguration();
    const token = await config.token();
    const response = await fetch(API + "/alt", {
      method: "POST",
      credentials: config.credentials,
      headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
      body: JSON.stringify({ mediaId: this.#media.getUnique() }),
    });
    if (!response.ok) {
      let detail = "";
      try { detail = (await response.json()).detail ?? ""; } catch { /* no body */ }
      window.alert(detail || `Request failed (${response.status})`);
      return;
    }
    const result = await response.json();
    const current = await this.#property.getValue();
    if (current && !window.confirm(`Replace the current alt text?\n\nCurrent: ${current}\n\nSuggested: ${result.suggestion}`)) return;
    this.#property.setValue(result.suggestion);
  }
}

export { AltTextPropertyAction as api };
