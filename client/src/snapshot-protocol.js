(() => {
  "use strict";

  const CLIENT_VERSION = "__SNAPSHOT_CLIENT_VERSION__";
  const PROTOCOL_VERSION = 1;
  const SCRIPT_ID = "snapshot-protocol";
  const ACTIVATION_QUERY_KEY = "snapshot-protocol";
  const RESTORE_ROUTE_QUERY_KEY = "snapshot-route";
  const READY_ELEMENT = "snapshot-ready";
  const PROCESSED_ATTRIBUTE = "data-snapshot-processed";
  const SITE_VERSION_META = "snapshot:site-version";
  const ROUTE_META = "snapshot:route";
  const DEFAULT_READY_TIMEOUT_MS = 30_000;

  const logPrefix = `[Snapshot Protocol ${CLIENT_VERSION}]`;
  const script = document.getElementById(SCRIPT_ID);
  const currentUrl = new URL(window.location.href);
  const sessionToken = currentUrl.searchParams.get(ACTIVATION_QUERY_KEY);

  restorePreservedRoute();

  if (sessionToken) {
    activateExecutorMode(sessionToken);
  } else {
    validateSnapshotVersion();
  }

  async function activateExecutorMode(token) {
    let startupReadiness = null;
    let firstRequest = true;

    window.addEventListener("message", async (event) => {
      const message = event?.data;
      if (!message || message.type !== "snapshot:navigate") return;
      if (event.source !== window || event.origin !== window.location.origin) return;
      if (message.sessionToken !== token || typeof message.requestId !== "string") return;

      const targetPath = normalizeTargetPath(message.targetPath);
      if (!targetPath) {
        postToExecutor({ type: "snapshot:error", sessionToken: token, requestId: message.requestId, code: "INVALID_ROUTE", message: "The executor supplied an invalid target path." });
        return;
      }

      const timeoutMs = normalizeTimeout(message.timeoutMs);
      try {
        let readiness;
        const canUseStartupReadiness = firstRequest && startupReadiness?.isConnected === true && targetPath === currentRoutePath();
        if (canUseStartupReadiness) {
          window.history.replaceState({}, "", targetPath);
          readiness = startupReadiness;
        } else {
          markCurrentReadinessConsumed();
          navigate(targetPath);
          readiness = await waitForReadiness(timeoutMs);
        }

        firstRequest = false;
        startupReadiness = null;
        readiness.setAttribute(PROCESSED_ATTRIBUTE, "true");
        readiness.style.display = "none";
        const rawHtml = document.documentElement.outerHTML;
        const html = transformSnapshot(rawHtml, targetPath);
        postToExecutor({
          type: "snapshot:result",
          sessionToken: token,
          requestId: message.requestId,
          capturedPath: currentRoutePath(),
          protocolVersion: PROTOCOL_VERSION,
          clientVersion: CLIENT_VERSION,
          siteVersion: getSiteVersion(script),
          html
        });
      } catch (error) {
        firstRequest = false;
        startupReadiness = null;
        postToExecutor({
          type: "snapshot:error",
          sessionToken: token,
          requestId: message.requestId,
          capturedPath: currentRoutePath(),
          code: error?.code ?? "SNAPSHOT_FAILED",
          message: error instanceof Error ? error.message : String(error)
        });
      }
    });

    startupReadiness = await waitForReadiness(null);
    window.__snapshotProtocolReady = true;
    postToExecutor({ type: "snapshot:client-ready", sessionToken: token, protocolVersion: PROTOCOL_VERSION, clientVersion: CLIENT_VERSION });
    console.info(`${logPrefix} Executor mode is ready.`);
  }

  function markCurrentReadinessConsumed() {
    document.querySelectorAll(READY_ELEMENT).forEach((element) => element.setAttribute(PROCESSED_ATTRIBUTE, "true"));
  }

  function navigate(targetPath) {
    if (currentRoutePath() !== targetPath) {
      window.history.pushState({}, "", targetPath);
      window.dispatchEvent(new PopStateEvent("popstate"));
    }
  }

  function waitForReadiness(timeoutMs) {
    return new Promise((resolve, reject) => {
      let completed = false;
      const selector = `${READY_ELEMENT}:not([${PROCESSED_ATTRIBUTE}="true"])`;
      const find = () => document.querySelector(selector);
      const finish = (value, error) => {
        if (completed) return;
        completed = true;
        observer.disconnect();
        clearInterval(poller);
        if (timeout !== null) clearTimeout(timeout);
        if (error) reject(error); else resolve(value);
      };
      const immediate = find();
      if (immediate) { resolve(immediate); return; }
      const observer = new MutationObserver(() => { const element = find(); if (element) finish(element); });
      const poller = setInterval(() => { const element = find(); if (element) finish(element); }, 100);
      const timeout = Number.isFinite(timeoutMs) && timeoutMs > 0
        ? setTimeout(() => {
            const error = new Error(`Timed out waiting for <${READY_ELEMENT}> after ${timeoutMs} ms.`);
            error.code = "READY_TIMEOUT";
            finish(undefined, error);
          }, timeoutMs)
        : null;
      observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: [PROCESSED_ATTRIBUTE] });
    });
  }

  function transformSnapshot(html, capturedRoute) {
    const parser = new DOMParser();
    const documentCopy = parser.parseFromString(html, "text/html");
    const head = documentCopy.head;
    head.querySelectorAll('script[type="application/ld+json"]').forEach((element) => element.remove());
    head.querySelectorAll("title").forEach((element) => element.remove());
    head.querySelectorAll('link[rel="canonical"]').forEach((element) => element.remove());
    head.querySelectorAll("meta").forEach((meta) => {
      const name = meta.getAttribute("name")?.toLowerCase();
      const allowed = meta.hasAttribute("charset") || meta.hasAttribute("http-equiv") || name === "viewport";
      if (!allowed) meta.remove();
    });
    const readiness = documentCopy.querySelector(READY_ELEMENT);
    if (!readiness) throw new Error(`Captured document did not contain <${READY_ELEMENT}>.`);
    head.appendChild(documentCopy.createTextNode("\n"));
    head.appendChild(documentCopy.createComment(" Snapshot Protocol metadata "));
    head.appendChild(documentCopy.createTextNode("\n"));
    const wrapper = documentCopy.createElement("div");
    wrapper.innerHTML = readiness.innerHTML.trim();
    Array.from(wrapper.childNodes).forEach((node) => head.appendChild(node));
    appendMeta(documentCopy, head, SITE_VERSION_META, getSiteVersion(script) ?? "");
    appendMeta(documentCopy, head, ROUTE_META, capturedRoute);
    head.appendChild(documentCopy.createTextNode("\n"));
    head.appendChild(documentCopy.createComment(" End Snapshot Protocol metadata "));
    head.appendChild(documentCopy.createTextNode("\n"));
    readiness.remove();
    return `<!doctype html>\n${documentCopy.documentElement.outerHTML}`;
  }

  function appendMeta(documentCopy, head, name, content) {
    const meta = documentCopy.createElement("meta");
    meta.setAttribute("name", name);
    meta.setAttribute("content", content);
    head.appendChild(meta);
  }

  function validateSnapshotVersion() {
    const snapshotVersion = document.querySelector(`meta[name="${SITE_VERSION_META}"]`)?.getAttribute("content")?.trim();
    if (snapshotVersion === undefined) return;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 30_000);
    fetch("/index.html", { cache: "no-store", signal: controller.signal })
      .then((response) => { if (!response.ok) throw new Error(`Root index returned HTTP ${response.status}.`); return response.text(); })
      .then((html) => {
        const parsed = new DOMParser().parseFromString(html, "text/html");
        const rootScript = parsed.getElementById(SCRIPT_ID);
        if (!rootScript) { console.warn(`${logPrefix} Root index.html does not contain #${SCRIPT_ID}; version validation was skipped.`); return; }
        const rootVersion = getSiteVersion(rootScript);
        const forceOrigin = rootScript.getAttribute("data-force-origin")?.trim().toLowerCase() === "true";
        if (forceOrigin || (rootVersion && snapshotVersion && rootVersion !== snapshotVersion)) {
          const route = `${currentRoutePath()}${window.location.hash}`;
          window.location.replace(`/?${RESTORE_ROUTE_QUERY_KEY}=${encodeURIComponent(route)}`);
        }
      })
      .catch((error) => { if (error?.name !== "AbortError") console.warn(`${logPrefix} Snapshot version validation failed; hydration will continue.`, error); })
      .finally(() => clearTimeout(timeout));
  }

  function restorePreservedRoute() {
    const route = currentUrl.searchParams.get(RESTORE_ROUTE_QUERY_KEY);
    if (!route) return;
    const target = normalizeTargetPath(route);
    if (!target) return;
    window.history.replaceState({}, "", target);
    const dispatch = () => window.dispatchEvent(new PopStateEvent("popstate"));
    if (document.readyState === "loading") window.addEventListener("DOMContentLoaded", dispatch, { once: true }); else queueMicrotask(dispatch);
  }

  function normalizeTargetPath(value) {
    if (typeof value !== "string" || value.length === 0) return null;
    try {
      const parsed = new URL(value, window.location.origin);
      if (parsed.origin !== window.location.origin) return null;
      return `${decodeURI(parsed.pathname)}${parsed.search}`;
    } catch { return null; }
  }

  function currentRoutePath() {
    try {
      return `${decodeURI(window.location.pathname)}${window.location.search}`;
    } catch {
      return `${window.location.pathname}${window.location.search}`;
    }
  }

  function normalizeTimeout(value) {
    if (Number.isFinite(value) && value > 0) return Math.floor(value);
    const configured = Number.parseInt(script?.getAttribute("data-ready-timeout-ms") ?? "", 10);
    return Number.isFinite(configured) && configured > 0 ? configured : DEFAULT_READY_TIMEOUT_MS;
  }

  function getSiteVersion(element) { return element?.getAttribute("data-site-version")?.trim() || null; }
  function postToExecutor(message) { window.postMessage(message, window.location.origin); }
})();
