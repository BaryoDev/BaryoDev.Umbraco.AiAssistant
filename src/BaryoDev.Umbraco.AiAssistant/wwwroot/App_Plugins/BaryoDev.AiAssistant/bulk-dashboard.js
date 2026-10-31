import { Base, call, esc, STYLE } from "./common.js";

const KINDS = [
  ["Polish", "Polish the wording"],
  ["Seo", "SEO title, description and share text"],
  ["Alt", "Alt text for images"],
  ["Translate", "Translate to another language"],
];

class AiAssistantBulkDashboard extends Base {
  #job = null;
  #jobs = [];
  #estimate = null;
  #error = "";
  #message = "";
  #busy = false;
  #timer = null;
  #configured = true;

  connectedCallback() {
    super.connectedCallback?.();
    this.#render();
    this.#refresh();
  }

  disconnectedCallback() {
    super.disconnectedCallback?.();
    clearTimeout(this.#timer);
  }

  async #refresh() {
    try {
      const status = await call(this, "/status");
      this.#configured = status.configured;
      this.#jobs = await call(this, "/bulk/jobs");
      const open = this.#job?.id ?? this.#jobs.find((j) => j.state === "Running" || j.state === "Stopped")?.id ?? this.#jobs[0]?.id;
      this.#job = open ? await call(this, `/bulk/jobs/${open}`) : null;
    } catch (e) {
      this.#error = e.message;
    }
    this.#render();
    clearTimeout(this.#timer);
    if (this.#job?.state === "Running") this.#timer = setTimeout(() => this.#refresh(), 3000);
  }

  #request(confirm) {
    const kind = this.querySelector("#ai-kind").value;
    const root = this.querySelector(kind === "Alt" ? "#ai-media" : "#ai-node");
    const picked = root?.selection?.[0] ?? root?.value ?? "";
    const type = this.querySelector("#ai-type")?.selection?.[0] ?? this.querySelector("#ai-type")?.value ?? "";
    return {
      kind,
      nodeId: picked.trim() || null,
      includeDescendants: this.querySelector("#ai-desc").checked,
      documentType: type.trim() || null,
      culture: this.querySelector("#ai-culture")?.value.trim() || null,
      overwrite: this.querySelector("#ai-overwrite")?.checked ?? false,
      confirm,
    };
  }

  async #act(fn) {
    this.#busy = true;
    this.#error = "";
    this.#message = "";
    this.#render();
    try { await fn(); } catch (e) { this.#error = e.message; }
    this.#busy = false;
    this.#render();
  }

  #doEstimate() {
    this.#estimate = null;
    return this.#act(async () => { this.#estimate = await call(this, "/bulk/estimate", "POST", this.#request(false)); });
  }

  #doStart() {
    const req = this.#request(true);
    const e = this.#estimate;
    const cost = e?.estimatedCost == null ? "an unknown cost (no price is set)" : `about ${e.estimatedCost.toFixed(2)} USD`;
    if (!confirm(`Start this run? It will use ${cost}. This is an estimate.`)) return;
    return this.#act(async () => {
      const s = await call(this, "/bulk/start", "POST", req);
      this.#estimate = null;
      this.#job = null;
      await this.#refresh();
      this.#message = `Started ${s.items} items.`;
    });
  }

  #item(action, itemId, method = "POST", body = undefined) {
    return this.#act(async () => {
      const r = await call(this, `/bulk/jobs/${this.#job.id}/${action.replace("{item}", itemId)}`, method, body);
      this.#job = r.job ?? r;
      if (r.result) this.#message = `Accepted ${r.result.accepted}. Skipped ${r.result.skippedFactsFailed} that failed the facts check.${r.result.failed ? ` ${r.result.failed} could not be saved: ${r.result.firstError}` : ""}`;
    });
  }

  #edit(item, field) {
    const text = prompt("Edit the suggestion", field.edited ?? field.suggestion ?? "");
    if (text === null) return;
    return this.#item("items/{item}", item.id, "PUT", { alias: field.alias, value: text });
  }

  #setup() {
    const hasDocPicker = customElements.get("umb-input-document") && customElements.get("umb-input-document-type");
    return `
      <div class="ai-box">
        <h3>New bulk run</h3>
        <div class="ai-row"><label>Job</label>
          <select id="ai-kind">${KINDS.map(([v, l]) => `<option value="${v}">${esc(l)}</option>`).join("")}</select></div>
        <div class="ai-row"><label>Content node</label>
          ${hasDocPicker ? `<umb-input-document id="ai-node" max="1"></umb-input-document>` : `<input id="ai-node" placeholder="Node key (GUID)">`}
          <label><input type="checkbox" id="ai-desc" checked style="min-width:0"> Include everything below it</label></div>
        <div class="ai-row"><label>Media item or folder (alt text)</label>
          ${customElements.get("umb-input-media") ? `<umb-input-media id="ai-media" max="1"></umb-input-media>` : `<input id="ai-media" placeholder="Media key (GUID)">`}
          <div class="ai-note">Alt text runs use this field. Leave it empty and the document type field to scan every Image item.</div></div>
        <div class="ai-row"><label>Or document type</label>
          ${hasDocPicker ? `<umb-input-document-type id="ai-type" max="1"></umb-input-document-type>` : `<input id="ai-type" placeholder="Alias or key">`}</div>
        <div class="ai-row"><label>Language</label><input id="ai-culture" placeholder="for example fil-PH"><div class="ai-note">Translate: the language to translate into. Other jobs: leave empty for every language, or enter one to limit the run to it.</div></div>
        <div class="ai-row"><label></label><label><input type="checkbox" id="ai-overwrite" style="min-width:0"> Replace text that already exists</label></div>
        <div class="ai-row">
          <button class="ai-btn" id="ai-estimate">Estimate cost</button>
          <button class="ai-btn primary" id="ai-start" disabled>Start</button>
        </div>
        <div id="ai-est"></div>
      </div>`;
  }

  #estimateText() {
    const e = this.#estimate;
    const cost = e.estimatedCost == null ? "unknown (no price is set)" : `${e.estimatedCost.toFixed(2)} USD`;
    return `<div class="ai-msg">${e.items} items on ${e.nodes} nodes${e.skipped ? `, ${e.skipped} skipped` : ""}${e.noAccess ? `, ${e.noAccess} left out because you cannot edit them` : ""}${e.truncated ? ", list cut at the limit" : ""}.
      About ${e.inputTokens.toLocaleString()} input and ${e.outputTokens.toLocaleString()} output tokens. Estimated cost ${cost}. This is an estimate.
      ${e.fitsCap ? "" : `<br><strong>${esc(e.capMessage)}</strong>`}</div>`;
  }

  #queue() {
    const j = this.#job;
    if (!j) return `<div class="ai-box"><h3>Results</h3><div class="ai-note">No runs yet.</div></div>`;
    const active = j.state === "Running" || j.state === "Stopped";
    const rows = j.items.map((i) => {
      const facts = { Passed: "Passed", Failed: "Failed", NotApplicable: "Not checked", Pending: "" }[i.facts];
      const fields = i.fields.map((f) => `<div><strong>${esc(f.alias)}</strong>
        <div class="ai-cols"><div class="ai-orig">${esc(f.sourceText ?? f.original)}</div><div class="ai-sug">${f.suggestion == null ? "" : esc(f.edited ?? f.suggestion)}</div></div></div>`).join("");
      const buttons = i.state === "Ready"
        ? `<button class="ai-btn" data-a="accept" data-i="${i.id}">Accept</button>
           ${i.fields.map((f) => `<button class="ai-btn" data-a="edit" data-i="${i.id}" data-f="${esc(f.alias)}">${i.fields.length > 1 ? `Edit ${esc(f.alias)}` : "Edit"}</button>`).join("")}
           <button class="ai-btn" data-a="reject" data-i="${i.id}">Reject</button>`
        : i.state === "Accepted" && !i.published && j.kind !== "Alt" ? `<button class="ai-btn" data-a="publish" data-i="${i.id}">Publish</button>` : "";
      return `<tr><td>${esc(i.name)}<div class="ai-note">${esc(i.state)}${i.published ? ", published" : ""}${i.reason ? `: ${esc(i.reason)}` : ""}</div></td>
        <td>${fields}</td>
        <td>${facts ? `Facts: ${facts}` : ""}${i.missing.length ? `<div class="ai-note">Missing: ${esc(i.missing.join(", "))}</div>` : ""}${i.notes?.length ? `<div class="ai-note">Length: ${esc(i.notes.join("; "))}</div>` : ""}</td>
        <td>${buttons}</td></tr>`;
    }).join("");
    return `<div class="ai-box">
      <h3>${esc(j.kind)} run: ${esc(j.state)}</h3>
      ${j.message ? `<div class="ai-warn">${esc(j.message)}</div>` : ""}
      <div class="ai-row">
        ${active ? `<button class="ai-btn" id="ai-cancel">Cancel run</button>` : ""}
        ${j.state === "Stopped" ? `<button class="ai-btn" id="ai-resume">Resume</button>` : ""}
        <button class="ai-btn primary" id="ai-acceptall" ${j.items.some((i) => i.state === "Ready") ? "" : "disabled"}>Accept all that passed the facts check</button>
      </div>
      <div class="ai-note">Accepting saves a draft. Publishing is a separate step. Suggestions that failed the facts check are never accepted in bulk.</div>
      <table class="ai-table"><thead><tr><th>Item</th><th>Original and suggestion</th><th>Facts check</th><th></th></tr></thead><tbody>${rows}</tbody></table>
    </div>`;
  }

  // The form is built once so a re-render never empties it before it is read.
  #render() {
    if (!this.querySelector("#ai-setup")) {
      this.innerHTML = `${STYLE}<style>.ai-cols{display:grid;grid-template-columns:1fr 1fr;gap:8px}.ai-orig,.ai-sug{white-space:pre-wrap;padding:6px;border:1px solid #ddd;max-height:200px;overflow:auto}.ai-sug{background:#f4f9f4}</style>
        <div class="ai-wrap"><div id="ai-top"></div><div id="ai-setup">${this.#setup()}</div><div id="ai-queue"></div></div>`;
      this.querySelector("#ai-estimate").onclick = () => this.#doEstimate();
      this.querySelector("#ai-start").onclick = () => this.#doStart();
    }
    this.querySelector("#ai-top").innerHTML = `
        ${this.#error ? `<div class="ai-err">${esc(this.#error)}</div>` : ""}
        ${this.#message ? `<div class="ai-msg">${esc(this.#message)}</div>` : ""}
        ${this.#configured ? "" : `<div class="ai-warn">Add an API key in Settings.</div>`}`;
    this.querySelector("#ai-estimate").disabled = this.#busy;
    const e = this.#estimate;
    this.querySelector("#ai-start").disabled = this.#busy || !e || !e.items || !e.fitsCap;
    this.querySelector("#ai-est").innerHTML = e ? this.#estimateText() : `<div class="ai-note">Pick what to include, then estimate the cost. Nothing is sent to Claude until you start.</div>`;
    this.querySelector("#ai-queue").innerHTML = this.#queue();
    const on = (sel, fn) => { const el = this.querySelector(sel); if (el) el.onclick = fn; };
    on("#ai-cancel", () => confirm("Cancel this run? Items already done stay in the list.") && this.#item("cancel"));
    on("#ai-resume", () => this.#item("resume").then(() => this.#refresh()));
    on("#ai-acceptall", () => confirm("Accept every ready suggestion that passed the facts check? Each is saved as a draft.") && this.#item("accept-all"));
    for (const b of this.querySelectorAll("button[data-a]")) {
      const item = this.#job.items.find((i) => i.id === b.dataset.i);
      b.onclick = () => {
        if (b.dataset.a === "edit") return this.#edit(item, item.fields.find((f) => f.alias === b.dataset.f) ?? item.fields[0]);
        if (b.dataset.a === "accept" && item.facts === "Failed") {
          if (!confirm("This suggestion failed the facts check. Accept it anyway? It is saved as a draft.")) return;
          return this.#item(`items/{item}/accept`, item.id, "POST", { acceptFailedFacts: true });
        }
        return this.#item(`items/{item}/${b.dataset.a}`, item.id);
      };
    }
  }
}

customElements.define("baryodev-ai-assistant-bulk", AiAssistantBulkDashboard);
export default AiAssistantBulkDashboard;
