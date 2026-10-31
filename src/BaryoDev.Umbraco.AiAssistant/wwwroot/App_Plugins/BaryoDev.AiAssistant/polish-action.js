import { UmbPropertyActionBase } from "@umbraco-cms/backoffice/property-action";
import { UMB_PROPERTY_CONTEXT } from "@umbraco-cms/backoffice/property";
import { UMB_DOCUMENT_WORKSPACE_CONTEXT } from "@umbraco-cms/backoffice/document";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";

const API = "/umbraco/management/api/v1/ai-assistant";

const escapeHtml = (s) => s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
const stripTags = (s) => s.replace(/<\/(p|div|li|h[1-6])>/gi, "\n").replace(/<[^>]+>/g, "").replace(/&nbsp;/g, " ");

// Word level diff (longest common subsequence). Returns [{ op: "same" | "add" | "del", text }].
function diffWords(a, b) {
  const x = a.split(/(\s+)/).filter(Boolean);
  const y = b.split(/(\s+)/).filter(Boolean);
  const n = x.length, m = y.length;
  const t = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--)
      t[i][j] = x[i] === y[j] ? t[i + 1][j + 1] + 1 : Math.max(t[i + 1][j], t[i][j + 1]);
  const out = [];
  let i = 0, j = 0;
  while (i < n && j < m) {
    if (x[i] === y[j]) { out.push({ op: "same", text: x[i] }); i++; j++; }
    else if (t[i + 1][j] >= t[i][j + 1]) out.push({ op: "del", text: x[i++] });
    else out.push({ op: "add", text: y[j++] });
  }
  while (i < n) out.push({ op: "del", text: x[i++] });
  while (j < m) out.push({ op: "add", text: y[j++] });
  return out;
}

// Every link, image and form address in some markup, so a changed one can be shown before Accept.
function urlsOf(html) {
  const doc = new DOMParser().parseFromString(html, "text/html");
  const found = new Set();
  for (const el of doc.body.querySelectorAll("*"))
    for (const a of ["href", "src", "action", "formaction", "poster", "cite", "data", "srcset"])
      if (el.hasAttribute(a)) found.add(`${el.localName} ${a}: ${el.getAttribute(a)}`);
  return found;
}

const markup = (parts, keep) =>
  parts.filter((p) => p.op === "same" || p.op === keep)
    .map((p) => (p.op === keep ? `<mark class="${keep}">${escapeHtml(p.text)}</mark>` : escapeHtml(p.text))).join("");

export default class PolishPropertyAction extends UmbPropertyActionBase {
  #property;
  #document;
  #auth;

  constructor(host, args) {
    super(host, args);
    this.consumeContext(UMB_PROPERTY_CONTEXT, (c) => (this.#property = c));
    this.consumeContext(UMB_DOCUMENT_WORKSPACE_CONTEXT, (c) => (this.#document = c));
    this.consumeContext(UMB_AUTH_CONTEXT, (c) => (this.#auth = c));
  }

  async #call(path, method, body) {
    const config = this.#auth.getOpenApiConfiguration();
    const token = await config.token();
    const response = await fetch(API + path, {
      method,
      credentials: config.credentials,
      headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
    if (!response.ok) {
      let detail = "";
      try { detail = (await response.json()).detail ?? ""; } catch { /* no body */ }
      throw new Error(detail || `Request failed (${response.status})`);
    }
    return response.json();
  }

  async execute() {
    const value = await this.#property.getValue();
    const isRich = value !== null && typeof value === "object" && "markup" in value;
    const source = isRich ? value.markup : (value ?? "");
    const data = this.#document.getData();
    const nodeId = this.#document.getUnique();
    const documentType = data?.documentType?.alias ?? data?.documentType?.unique ?? "";
    const property = this.#property.getAlias();

    const dialog = document.createElement("div");
    dialog.setAttribute("style", "position:fixed;inset:0;z-index:10000;width:100vw;height:100vh;max-width:none;margin:0;padding:0;border:0;overflow:auto;background:rgba(0,0,0,.45);display:flex;align-items:center;justify-content:center;");
    dialog.innerHTML = `
      <style>
        .box{background:#fff;color:#222;border-radius:6px;width:min(1100px,94vw);max-height:90vh;display:flex;flex-direction:column;font-family:inherit}
        .box header,.box footer{padding:14px 20px}.box header{font-weight:bold;border-bottom:1px solid #ddd}
        .box footer{border-top:1px solid #ddd;display:flex;gap:8px;justify-content:flex-end;align-items:center}
        .cols{display:grid;grid-template-columns:1fr 1fr;gap:16px;padding:16px 20px;overflow:auto}
        .cols h4{margin:0 0 6px}.pane{white-space:pre-wrap;border:1px solid #ddd;border-radius:4px;padding:10px;min-height:120px}
        textarea.pane{width:100%;box-sizing:border-box;font:inherit;resize:vertical}
        mark.add{background:#c8f0c8}mark.del{background:#f6c9c9;text-decoration:line-through}
        .warn{margin:0 20px;padding:8px 12px;background:#fff3cd;border:1px solid #e0c36a;border-radius:4px}
        .extra{padding:0 20px;font-size:.9em}.msg{padding:20px}
        button{padding:7px 14px;border:1px solid #888;border-radius:4px;background:#f4f4f4;cursor:pointer}
        button.primary{background:#1b264f;color:#fff;border-color:#1b264f}
      </style>
      <div class="box"><header>Polish with Claude</header><div class="msg">Working on it...</div></div>`;
    document.body.appendChild(dialog);
    // The property action menu is a popover in the top layer, so only another top layer element can sit above it.
    try { dialog.setAttribute("popover", "manual"); dialog.showPopover(); } catch { /* z-index stays */ }
    const box = dialog.querySelector(".box");
    const close = () => dialog.remove();

    try {
      const result = await this.#call("/polish", "POST", {
        nodeId, documentType, property, text: source, isRichText: isRich, wantTitle: false, wantSummary: false,
      });
      const plainSource = isRich ? stripTags(source) : source;
      const plainSuggestion = isRich ? stripTags(result.suggestion) : result.suggestion;
      const parts = diffWords(plainSource, plainSuggestion);
      const warning = result.flagged
        ? `<div class="warn">Check the facts. These were in the original but not in the suggestion: <b>${result.missing.map(escapeHtml).join(", ")}</b></div>` : "";
      box.innerHTML = `
        <header>Polish with Claude</header>
        ${warning}
        <div class="cols">
          <div><h4>Original</h4><div class="pane">${markup(parts, "del")}</div></div>
          <div><h4>Suggestion</h4><div class="pane" id="preview"></div>
            <details style="margin-top:8px"><summary>Edit the suggestion${isRich ? " (HTML)" : ""}</summary><textarea class="pane" id="edit" rows="8"></textarea></details></div>
        </div>
        <div class="warn" id="urls" hidden></div>
        <div class="extra">Changes: <mark class="add">added</mark> <mark class="del">removed</mark>. Nothing is saved until you accept and save the page.</div>
        <footer><button id="discard">Discard</button><button class="primary" id="accept">Accept</button></footer>`;
      const editor = box.querySelector("#edit");
      editor.value = result.suggestion;
      const preview = box.querySelector("#preview");
      const showPreview = () => {
        const now = isRich ? stripTags(editor.value) : editor.value;
        preview.innerHTML = markup(diffWords(plainSource, now), "add");
      };
      const urlBox = box.querySelector("#urls");
      const showUrls = () => {
        if (!isRich) return;
        const before = urlsOf(source);
        const added = [...urlsOf(editor.value)].filter((u) => !before.has(u));
        urlBox.hidden = added.length === 0;
        urlBox.innerHTML = added.length ? `Links or images added or changed. Check them before you accept:<ul>${added.map((u) => `<li>${escapeHtml(u)}</li>`).join("")}</ul>` : "";
      };
      showPreview();
      showUrls();
      editor.oninput = () => { showPreview(); showUrls(); };
      box.querySelector("#discard").onclick = close;
      box.querySelector("#accept").onclick = () => {
        const text = editor.value;
        this.#property.setValue(isRich ? { ...value, markup: text } : text);
        close();
      };
    } catch (error) {
      box.innerHTML = `<header>Polish with Claude</header><div class="msg">${escapeHtml(error.message)}</div><footer><button id="ok">Close</button></footer>`;
      box.querySelector("#ok").onclick = close;
    }
  }
}

export { PolishPropertyAction as api };
