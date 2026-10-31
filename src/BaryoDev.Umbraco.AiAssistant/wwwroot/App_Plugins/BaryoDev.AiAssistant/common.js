// Shared by the dashboards. Plain custom elements with no build step, the same approach as the
// PWA dashboard: Umbraco 17 calls provideContext() on a dashboard element as it mounts, which a bare
// HTMLElement lacks, so the base comes from the backoffice's own UmbElementMixin.
export const API = "/umbraco/management/api/v1/ai-assistant";

export const Base = await import("@umbraco-cms/backoffice/element-api")
  .then(({ UmbElementMixin }) => UmbElementMixin(HTMLElement))
  .catch(() => HTMLElement);

export function esc(value) {
  return String(value ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
}

const tokens = new WeakMap();

async function token(host) {
  if (!tokens.has(host)) {
    tokens.set(host, Promise.all([
      import("@umbraco-cms/backoffice/context-api"),
      import("@umbraco-cms/backoffice/auth"),
    ]).then(([{ UmbContextConsumer }, { UMB_AUTH_CONTEXT }]) => {
      const consumer = new UmbContextConsumer(host, UMB_AUTH_CONTEXT);
      const auth = consumer.asPromise();
      consumer.request();
      return auth;
    }).catch(() => null));
  }
  const auth = await tokens.get(host);
  return auth ? auth.getLatestToken() : null;
}

/** Calls the management API with the signed-in user's token. Throws an Error with the server's plain message. */
export async function call(host, path, method = "GET", body = undefined) {
  const t = await token(host);
  const headers = { ...(t ? { Authorization: `Bearer ${t}` } : {}), ...(body === undefined ? {} : { "Content-Type": "application/json" }) };
  const response = await fetch(API + path, { method, credentials: "same-origin", headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (response.status === 401 || response.status === 403) throw new Error("You do not have access to this.");
  if (!response.ok) {
    let detail = "";
    try { detail = (await response.json()).detail ?? ""; } catch { /* no body */ }
    throw new Error(detail || `Request failed (${response.status}).`);
  }
  return (response.headers.get("content-type") ?? "").includes("json") ? response.json() : null;
}

export const STYLE = `
  <style>
    .ai-wrap { padding: var(--uui-size-layout-1, 20px); display: grid; gap: 16px; max-width: 960px; }
    .ai-box { background: var(--uui-color-surface, #fff); border: 1px solid var(--uui-color-border, #d8d7d9); border-radius: 3px; padding: 16px; display: grid; gap: 10px; }
    .ai-box h3 { margin: 0; }
    .ai-row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    .ai-row label { min-width: 150px; }
    .ai-row input, .ai-row select, .ai-row textarea { flex: 1; min-width: 220px; padding: 6px; font: inherit; }
    .ai-note { color: var(--uui-color-text-alt, #68676b); font-size: 13px; }
    .ai-msg { padding: 8px 12px; background: #eef3ff; border: 1px solid #b9c8f0; border-radius: 3px; }
    .ai-warn { padding: 8px 12px; background: #fff3cd; border: 1px solid #e0c36a; border-radius: 3px; }
    .ai-err { padding: 8px 12px; background: #fde8e8; border: 1px solid #e0a0a0; border-radius: 3px; }
    button.ai-btn { padding: 6px 14px; border: 1px solid #888; border-radius: 3px; background: #f4f4f4; cursor: pointer; font: inherit; }
    button.ai-btn.primary { background: #1b264f; color: #fff; border-color: #1b264f; }
    button.ai-btn:disabled { opacity: .5; cursor: default; }
    table.ai-table { border-collapse: collapse; width: 100%; }
    table.ai-table th, table.ai-table td { text-align: left; padding: 6px; border-bottom: 1px solid #ddd; vertical-align: top; }
  </style>`;
