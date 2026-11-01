// Owner: integration. The browser boundary of Causa (KD-010): it loads the .NET WebAssembly engine, wraps its one
// [JSExport] in a Limen EngineTransport and starts the Limen BrowserKernel. It keeps no application state and makes
// no application decision: what is shown comes from the engine's projection (Causa.App View.fs).
//
// Page states (html[data-engine]): "loading" until the engine's first projection says engineReady, then "ready";
// "failed" when the runtime cannot start or the start exceeds the deadline (WASM-LOAD-003/004/005).
import { BrowserKernel } from "@echelon-foundry/limen";
import { scheduleCapability } from "@echelon-foundry/limen/capabilities/schedule";
import { filesCapability } from "@echelon-foundry/limen/capabilities/files";

/** How long the engine may take to answer Initialize before the page offers Retry. */
export const startDeadlineMs = 30_000;

/** Pure: the page state a Limen answer implies. */
export const pageStateOf = (answer) => (answer?.view?.engineReady === true ? "ready" : "loading");

/** Pure: an EngineTransport over a string-in/string-out engine function, reporting each answer to `observe`. */
export const jsonTransport = (start, observe) => {
  let dispatchJson = null;
  return {
    async start() {
      dispatchJson = await start();
    },
    async dispatch(message) {
      const answer = JSON.parse(dispatchJson(JSON.stringify(message)));
      observe(answer);
      return answer;
    }
  };
};

/** Loads the .NET runtime from ./_framework and returns the engine's Dispatch export. */
export const startDotnetEngine = async (frameworkUrl = "./_framework/dotnet.js") => {
  const { dotnet } = await import(frameworkUrl);
  const runtime = await dotnet.create();
  const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
  return (json) => exports.Causa.Host.HostShim.Dispatch(json);
};

const withDeadline = (promise, ms) =>
  Promise.race([
    promise,
    new Promise((_, reject) => setTimeout(() => reject(new Error("engine start deadline exceeded")), ms))
  ]);

const show = (doc, state) => {
  doc.documentElement.dataset.engine = state;
  const loading = doc.querySelector("[data-causa-loading]");
  const app = doc.querySelector("[data-causa-app]");
  const failed = doc.querySelector("[data-causa-load-failed]");
  const status = doc.querySelector("[data-causa-load-status]");
  if (app) app.hidden = state !== "ready";
  if (loading) loading.hidden = state === "ready";
  if (failed) failed.hidden = state !== "failed";
  if (status) status.textContent = state === "failed" ? "Causa could not start." : "Starting Causa…";
};

/**
 * WI-R8-T23: hands the engine-projected Forma workflow document (data-workflow, bound by Limen from Views/Graph.fs) to
 * its <forma-workflow> element whenever the projection changes. The element lays it out; nothing here decides content.
 */
export const followWorkflows = (doc) => {
  const loaded = new WeakMap();
  const feed = (element) => {
    const text = element.getAttribute("data-workflow");
    if (!text || loaded.get(element) === text || typeof element.load !== "function") return;
    loaded.set(element, text);
    element.load(text).catch((error) => console.error("Causa graph:", error));
  };
  const feedAll = () => doc.querySelectorAll("forma-workflow[data-workflow]").forEach(feed);
  new doc.defaultView.MutationObserver((records) => records.forEach((record) => feed(record.target))).observe(doc.body, {
    subtree: true,
    attributes: true,
    attributeFilter: ["data-workflow"]
  });
  doc.defaultView.customElements.whenDefined("forma-workflow").then(feedAll);
};

/**
 * WI-R8-T26: the report's print export. "Print or save as PDF" opens the browser's print dialog, where Folio's print
 * stylesheet lays out the engine-projected report; every print (the button or the browser's own command) stamps the
 * report's generation time first (UI-009-O2). Nothing here decides report content.
 */
export const followPrint = (doc) => {
  const view = doc.defaultView;
  const stamp = () =>
    doc.querySelectorAll("[data-causa-report-generated]").forEach((element) => {
      element.textContent = new Date().toISOString();
    });
  view.addEventListener("beforeprint", stamp);
  doc.addEventListener("click", (event) => {
    if (!event.target.closest?.("[data-causa-print]")) return;
    stamp();
    view.print();
  });
};

/**
 * WI-R8-CAPS: the optional Limen capabilities this host offers. The engine selects them in its handshake answer to
 * Initialize and then asks for timeouts (the save machine's automatic reconciliation reads) and downloads (Export my
 * changes) as capability effects. Mechanism only: when and what is decided by the engine.
 */
export const hostCapabilities = () => [scheduleCapability(), filesCapability()];

/** Pure: the Echelon runtime registry (window.__ECHELON_RUNTIME__) to the report the engine reads, name to version. */
export const runtimeReportOf = (registry) => {
  const libraries = registry && typeof registry.libraries === "object" && registry.libraries !== null ? registry.libraries : {};
  return JSON.stringify({
    libraries: Object.fromEntries(
      Object.entries(libraries)
        .filter(([, entry]) => typeof entry?.version === "string")
        .map(([name, entry]) => [name, { version: entry.version }])
    )
  });
};

/**
 * WI-R8-T29: reports, as data, which Echelon libraries registered themselves on this page (the Limen event
 * "runtime.reported" bound in index.html). The engine decides what Administration shows (Views/Administration.fs).
 */
export const reportRuntime = (doc) => {
  const input = doc.querySelector("[data-causa-runtime-report]");
  if (!input) return;
  input.value = runtimeReportOf(doc.defaultView?.__ECHELON_RUNTIME__);
  input.dispatchEvent(new Event("change", { bubbles: true }));
};

/** Boots the page: loading shell, then the engine through Limen, then handoff or failure. */
export const boot = async (doc = document, startEngine = startDotnetEngine) => {
  show(doc, "loading");
  followWorkflows(doc);
  followPrint(doc);
  doc.querySelector("[data-causa-retry]")?.addEventListener("click", () => doc.defaultView?.location.reload());
  let ready = false;
  let started = null;
  const firstAnswer = new Promise((resolve) => (started = resolve));
  const transport = jsonTransport(startEngine, (answer) => {
    if (!ready && pageStateOf(answer) === "ready") {
      ready = true;
      show(doc, "ready");
    }
    started(answer);
  });
  const kernel = new BrowserKernel(transport, doc, {
    report(event) {
      if (event.kind === "BridgeError") {
        console.error("Causa boundary:", event.phase, event.detail);
        if (!ready) show(doc, "failed");
      }
    }
  }, { capabilities: hostCapabilities() });
  try {
    await withDeadline(Promise.all([kernel.start(), firstAnswer]), startDeadlineMs);
    if (!ready) show(doc, "failed");
    else reportRuntime(doc);
  } catch (error) {
    console.error("Causa boundary:", error);
    if (!ready) show(doc, "failed");
  }
  return kernel;
};

if (typeof document !== "undefined" && !globalThis.__CAUSA_NO_AUTOBOOT__) boot();
