import { Base, call, esc, STYLE } from "./common.js";

const SOURCE_NOTE = {
  configuration: "Set in configuration. Configuration wins, so the box here is ignored until that setting is removed.",
  dashboard: "Saved here.",
  default: "Not set. The default applies.",
};

const FIELDS = ["#ai-key", "#ai-model", "#ai-cap", "#ai-in", "#ai-out", "#ai-rules"];
const SAVE_FIELDS = FIELDS.filter((f) => f !== "#ai-rules");

class AiAssistantSettingsDashboard extends Base {
  #s = null;
  #message = "";
  #error = "";
  #busy = false;
  #draft = null;

  connectedCallback() {
    super.connectedCallback?.();
    this.#render();
    this.#load();
  }

  async #load() {
    try {
      this.#s = await call(this, "/settings");
      this.#error = "";
    } catch (e) {
      this.#error = e.message;
    }
    this.#render();
  }

  #typed() {
    return FIELDS.map((f) => [f, this.querySelector(f)?.value]);
  }

  #restore(typed, settled = []) {
    for (const [f, value] of typed) {
      const el = this.querySelector(f);
      if (el && value !== undefined && !settled.includes(f)) el.value = value;
    }
  }

  // Rendering rebuilds the form, so what was typed is put back. fn returns the fields it saved, which
  // show the server's value instead.
  async #act(label, fn) {
    const typed = this.#typed();
    let settled = [];
    this.#busy = true;
    this.#message = "";
    this.#error = "";
    this.#render();
    this.#restore(typed);
    try {
      settled = (await fn()) ?? [];
    } catch (e) {
      this.#error = `${label}: ${e.message}`;
    }
    this.#busy = false;
    this.#render();
    this.#restore(typed, settled);
  }

  #keyValue() {
    return this.querySelector("#ai-key")?.value ?? "";
  }

  #save() {
    const key = this.#s?.keySource === "configuration" ? "" : this.#keyValue().trim();
    const model = this.querySelector("#ai-model").value;
    const num = (id) => {
      const v = this.querySelector(id)?.value.trim();
      return v ? Number(v) : null;
    };
    const body = { model, monthlyCap: num("#ai-cap"), inputPrice: num("#ai-in"), outputPrice: num("#ai-out") };
    if ([body.monthlyCap, body.inputPrice, body.outputPrice].some((n) => n !== null && !(n >= 0))) {
      this.#error = "Cap and prices must be numbers of zero or more.";
      return this.#render();
    }
    return this.#act("Save failed", async () => {
      if (key) this.#s = await call(this, "/settings/key", "PUT", { key });
      this.#s = await call(this, "/settings", "PUT", body);
      this.#message = this.#s?.keySource === "none" ? "Saved. No API key is set, so nothing will run until you add one." : "Saved.";
      return SAVE_FIELDS;
    });
  }

  #saveRules() {
    const rules = this.querySelector("#ai-rules").value;
    return this.#act("Saving the rules failed", async () => {
      this.#s = await call(this, "/voice/rules", "PUT", { rules });
      this.#draft = null;
      this.#message = "Rules saved.";
      return ["#ai-rules"];
    });
  }

  #restoreRules() {
    if (!confirm("Remove the saved rules and use the default?")) return;
    return this.#act("Restoring the rules failed", async () => {
      this.#s = await call(this, "/voice/rules", "PUT", { rules: "" });
      this.#draft = null;
      this.#message = "The default rules apply.";
      return ["#ai-rules"];
    });
  }

  #draftRules() {
    const picker = this.querySelector("#ai-articles");
    const ids = (picker?.selection ?? []).filter(Boolean);
    if (ids.length === 0 || ids.length > 10) {
      this.#error = "Choose between 1 and 10 articles.";
      return this.#render();
    }
    return this.#act("Drafting failed", async () => {
      const draft = await call(this, "/voice/draft", "POST", { nodeIds: ids });
      this.#draft = draft.rules;
      this.#message = `Draft ready.${draft.skipped ? ` ${draft.skipped} chosen page${draft.skipped === 1 ? " was" : "s were"} left out because ${draft.skipped === 1 ? "it has" : "they have"} no text in the source language.` : ""} Read it, edit it, then click Save rules. Nothing is saved yet.`;
      return ["#ai-rules"];
    });
  }

  #test() {
    const key = this.#s?.keySource === "configuration" ? null : this.#keyValue().trim() || null;
    return this.#act("Test failed", async () => {
      const result = await call(this, "/settings/test", "POST", { key });
      this.#message = result.ok ? `${result.message}${key ? " The key is not saved yet. Click Save to keep it." : ""}` : "";
      this.#error = result.ok ? "" : result.message;
    });
  }

  #remove() {
    if (!confirm("Remove the saved key? A key set in configuration is not affected.")) return;
    return this.#act("Remove failed", async () => {
      this.#s = await call(this, "/settings/key", "DELETE");
      this.#message = "The saved key was removed.";
      return ["#ai-key"];
    });
  }

  #render() {
    const s = this.#s;
    let keyState = "Loading.";
    if (s) {
      if (s.keySource === "configuration") keyState = "A key is set in configuration. It is used instead of any key saved here.";
      else if (s.keySource === "dashboard") keyState = `A key is saved here${s.keySavedBy ? ` by ${esc(s.keySavedBy)}` : ""}.`;
      else keyState = "No key is set. Add an API key below.";
    }
    const u = s?.usage;
    const money = (n) => (n == null ? "unknown" : `${n.toFixed(2)} USD`);
    this.innerHTML = `${STYLE}
      <div class="ai-wrap">
        ${this.#error ? `<div class="ai-err">${esc(this.#error)}</div>` : ""}
        ${this.#message ? `<div class="ai-msg">${esc(this.#message)}</div>` : ""}
        ${s?.keyLost ? `<div class="ai-warn">${esc(s.keyMessage)}</div>` : ""}
        <div class="ai-box">
          <h3>Claude API key</h3>
          <div>${keyState}</div>
          ${s && s.keySource === "none" ? `<div class="ai-note">
            <a href="https://platform.claude.com/" target="_blank" rel="noopener noreferrer">Get an API key</a>
            <ol>
              <li>Create a key in your Anthropic account.</li>
              <li>Paste it in the box below.</li>
              <li>Click Test.</li>
              <li>Click Save.</li>
            </ol>
            Usage is billed to your own Anthropic account.
          </div>` : ""}
          <div class="ai-note">${s?.keySource === "configuration" ? "The key is set in configuration. Change it there. Test checks that key." : "The key is stored encrypted in the site database and is never shown again. Enter a new key to replace it."}</div>
          <div class="ai-row"><label for="ai-key">API key</label>
            <input id="ai-key" type="password" autocomplete="off" ${s?.keySource === "configuration" ? "disabled" : ""} placeholder="${s?.keySource === "dashboard" ? "Saved. Enter a new key to replace it." : "Paste your key"}"></div>
          <div class="ai-row"><label for="ai-model">Model</label>
            <input id="ai-model" value="${esc(s && s.modelSource === "dashboard" ? s.model : "")}" placeholder="${esc(s?.model ?? "")}" ${s?.modelSource === "configuration" ? "disabled" : ""}></div>
          <div class="ai-note">${s ? esc(SOURCE_NOTE[s.modelSource]) : ""} Using: ${esc(s?.model ?? "")}</div>
          <div class="ai-row">
            <button class="ai-btn" id="ai-test" ${this.#busy ? "disabled" : ""}>Test</button>
            <button class="ai-btn primary" id="ai-save" ${this.#busy ? "disabled" : ""}>Save</button>
            <button class="ai-btn" id="ai-remove" ${this.#busy || s?.keySource !== "dashboard" ? "disabled" : ""}>Remove</button>
          </div>
        </div>
        <div class="ai-box">
          <h3>Usage and spend cap</h3>
          <div>${u ? `${esc(u.month)}: ${u.calls} calls, ${u.inputTokens.toLocaleString()} input tokens, ${u.outputTokens.toLocaleString()} output tokens. Estimated cost ${money(u.estimatedCost)}${u.unpricedCalls ? `, not counting ${u.unpricedCalls} calls made with no price set` : ""}.` : "Loading."}</div>
          <div class="ai-note">Costs are estimates from the prices below, not a bill. Check your Anthropic account for the real figure.</div>
          <div class="ai-row"><label for="ai-cap">Monthly cap (USD)</label>
            <input id="ai-cap" type="number" min="0" step="0.01" value="${s?.capSource === "dashboard" ? s.monthlyCap : ""}" placeholder="${s?.monthlyCap ?? "No cap"}" ${s?.capSource === "configuration" ? "disabled" : ""}></div>
          <div class="ai-note">${s?.capSource === "configuration" ? "Set in configuration. Configuration wins." : "Calls stop when the estimated spend reaches the cap. Empty means no cap."}</div>
          <div class="ai-row"><label for="ai-in">Input price per million tokens (USD, estimate)</label>
            <input id="ai-in" type="number" min="0" step="0.01" value="${s?.priceSource === "dashboard" ? (s.inputPrice ?? "") : ""}" placeholder="${s?.inputPrice ?? ""}" ${s?.priceSource === "configuration" || s?.capSource === "configuration" ? "disabled" : ""}></div>
          <div class="ai-row"><label for="ai-out">Output price per million tokens (USD, estimate)</label>
            <input id="ai-out" type="number" min="0" step="0.01" value="${s?.priceSource === "dashboard" ? (s.outputPrice ?? "") : ""}" placeholder="${s?.outputPrice ?? ""}" ${s?.priceSource === "configuration" || s?.capSource === "configuration" ? "disabled" : ""}></div>
          <div class="ai-note">${s?.capSource === "configuration" && s?.priceSource !== "configuration" ? "The cap is set in configuration, so prices here are ignored. Set the prices in configuration, or the suggested price for the model is used." : s?.priceSource === "configuration" ? "Set in configuration. Configuration wins." : s?.priceSource === "suggested" ? "Showing a suggested price for this model. Prices change, so check Anthropic's pricing page and enter your own." : s?.priceSource === "none" ? "No price is known for this model. Enter one to see costs or to use a cap." : "Saved here."}</div>
        </div>
        <div class="ai-box">
          <h3>Brand voice</h3>
          <div class="ai-note">${s?.rulesSource === "configuration" ? "Set in configuration. Configuration wins, so the box below is read only." : s?.rulesSource === "dashboard" ? "Saved here." : "Not set. The default rules apply."}</div>
          <textarea id="ai-rules" rows="9" style="width:100%" ${s?.rulesSource === "configuration" ? "disabled" : ""}>${esc(this.#draft ?? (s?.rulesSource === "default" ? "" : s?.rules ?? ""))}</textarea>
          ${s?.rulesSource === "default" ? `<div class="ai-note">Default rules: ${esc(s.defaultRules ?? "")}</div>` : ""}
          <div class="ai-note">The rules about facts and the answer format always apply and cannot be removed here.</div>
          <div class="ai-row"><label>Articles to learn from (up to 10)</label>
            <umb-input-document id="ai-articles" max="10"></umb-input-document></div>
          <div class="ai-note">Draft from my articles sends the text of the chosen pages to Claude and proposes rules. You review them before saving.</div>
          <div class="ai-row">
            <button class="ai-btn" id="ai-draft" ${this.#busy || s?.rulesSource === "configuration" || !s?.keySource || s.keySource === "none" ? "disabled" : ""}>Draft from my articles</button>
            <button class="ai-btn primary" id="ai-rules-save" ${this.#busy || s?.rulesSource === "configuration" ? "disabled" : ""}>Save rules</button>
            <button class="ai-btn" id="ai-rules-restore" ${this.#busy || s?.rulesSource !== "dashboard" ? "disabled" : ""}>Restore default</button>
          </div>
        </div>
      </div>`;
    this.querySelector("#ai-test").onclick = () => this.#test();
    this.querySelector("#ai-save").onclick = () => this.#save();
    this.querySelector("#ai-remove").onclick = () => this.#remove();
    this.querySelector("#ai-draft").onclick = () => this.#draftRules();
    this.querySelector("#ai-rules-save").onclick = () => this.#saveRules();
    this.querySelector("#ai-rules-restore").onclick = () => this.#restoreRules();
  }
}

customElements.define("baryodev-ai-assistant-settings", AiAssistantSettingsDashboard);
export default AiAssistantSettingsDashboard;
