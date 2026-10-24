import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import test from "node:test";
import vm from "node:vm";

const extensionDirectory = resolve(dirname(fileURLToPath(import.meta.url)), "..");

async function createHarness() {
  const source = await readFile(resolve(extensionDirectory, "service-worker.js"), "utf8");
  const manifest = JSON.parse(await readFile(resolve(extensionDirectory, "manifest.json"), "utf8"));
  const listeners = {};
  const menus = [];
  const messages = [];
  const stored = {};
  const pausedDownloads = new Set();
  const cancelledDownloads = new Set();
  const downloads = [
    {
      id: "1b736060-f38e-4ee3-9ef2-0c991b13f805",
      fileName: "archive.zip",
      source: "files.example",
      status: "Downloading",
      progress: "40 B / 100 B",
      size: "100 B",
      rate: "12 B/s",
      timeLeft: "5s"
    }
  ];
  let failToConnect = false;
  let interceptionDecision = "desktop";
  let failToCancel = false;
  let handoffRetriesRemaining = 0;

  const chrome = {
    runtime: {
      onInstalled: { addListener: (listener) => { listeners.installed = listener; } },
      onMessage: { addListener: (listener) => { listeners.message = listener; } },
      sendNativeMessage: async (host, request) => {
        messages.push({ host, request });
        if (failToConnect) throw new Error("Native host unavailable");
        if (request.type === "download.handoffComplete" && handoffRetriesRemaining > 0) {
          handoffRetriesRemaining--;
          throw new Error("Temporary native host error");
        }
        let result;
        switch (request.type) {
          case "desktop.status":
            result = { ready: true, downloadCount: downloads.length };
            break;
          case "downloads.list":
            result = downloads;
            break;
          case "download.add":
            result = { id: downloads[0].id, fileName: "archive.zip", status: "Queued" };
            break;
          case "download.intercept":
            result = {
              decision: interceptionDecision,
              id: downloads[0].id,
              fileName: request.fileName
            };
            break;
          default:
            result = { success: true };
        }
        return { requestId: request.requestId, ok: true, result };
      }
    },
    action: {
      setBadgeBackgroundColor: async () => {},
      setBadgeText: async () => {},
      setTitle: async () => {}
    },
    contextMenus: {
      onClicked: { addListener: (listener) => { listeners.menuClicked = listener; } },
      removeAll: (callback) => callback(),
      create: (menu) => menus.push(menu)
    },
    storage: {
      local: {
        get: async (key) => ({ [key]: stored[key] }),
        set: async (value) => Object.assign(stored, value)
      }
    },
    downloads: {
      onCreated: { addListener: (listener) => { listeners.downloadCreated = listener; } },
      pause: async (id) => { pausedDownloads.add(id); },
      resume: async (id) => { pausedDownloads.delete(id); },
      cancel: async (id) => {
        if (failToCancel) throw new Error("Download could not be cancelled");
        cancelledDownloads.add(id);
        pausedDownloads.delete(id);
      }
    }
  };

  vm.runInNewContext(source, {
    chrome,
    URL,
    console,
    Promise,
    Set,
    Array,
    Number,
    setTimeout,
    crypto: { randomUUID: () => "unit-test-request-id" }
  });

  return {
    manifest,
    menus,
    listeners,
    messages,
    downloads,
    stored,
    pausedDownloads,
    cancelledDownloads,
    set failToConnect(value) { failToConnect = value; },
    set interceptionDecision(value) { interceptionDecision = value; },
    set failToCancel(value) { failToCancel = value; },
    set handoffRetriesRemaining(value) { handoffRetriesRemaining = value; },
    async send(message) {
      return await new Promise((resolveResponse) => {
        const keepsChannelOpen = listeners.message(message, {}, resolveResponse);
        assert.equal(keepsChannelOpen, true);
      });
    }
  };
}

test("declares download interception permissions without all-sites host access", async () => {
  const { manifest } = await createHarness();
  assert.equal(manifest.manifest_version, 3);
  assert.deepEqual([...manifest.permissions].sort(), [
    "activeTab",
    "contextMenus",
    "downloads",
    "nativeMessaging",
    "scripting",
    "storage"
  ]);
  assert.equal("host_permissions" in manifest, false);
});

test("auto-intercepts ordinary browser downloads using pause, desktop handoff, then cancel", async () => {
  const harness = await createHarness();
  harness.listeners.downloadCreated({
    id: 9,
    state: "in_progress",
    url: "https://files.example/archive.zip?token=private",
    filename: "C:\\Users\\Sammy\\Downloads\\archive.zip",
    byExtensionId: ""
  });
  await new Promise((resolveDelay) => setTimeout(resolveDelay, 0));
  await new Promise((resolveDelay) => setTimeout(resolveDelay, 0));

  assert.equal(harness.messages[0].request.type, "download.intercept");
  assert.equal(harness.messages[0].request.url, "https://files.example/archive.zip?token=private");
  assert.equal(harness.messages[0].request.fileName, "archive.zip");
  assert.equal(harness.pausedDownloads.has(9), false);
  assert.equal(harness.cancelledDownloads.has(9), true);
  assert.equal(harness.messages[1].request.type, "download.handoffComplete");
});

test("retries the desktop queue commit after cancelling the Chrome copy", async () => {
  const harness = await createHarness();
  harness.handoffRetriesRemaining = 1;
  harness.listeners.downloadCreated({
    id: 14,
    state: "in_progress",
    url: "https://files.example/archive.zip",
    filename: "archive.zip"
  });
  await new Promise((resolveDelay) => setTimeout(resolveDelay, 350));

  const commits = harness.messages.filter(({ request }) =>
    request.type === "download.handoffComplete");
  assert.equal(commits.length, 2);
  assert.equal(harness.cancelledDownloads.has(14), true);
  assert.equal(harness.pausedDownloads.has(14), false);
});

test("can leave a browser download in Chrome when interception is disabled", async () => {
  const harness = await createHarness();
  harness.stored.browserInterceptionEnabled = false;
  harness.listeners.downloadCreated({
    id: 10,
    state: "in_progress",
    url: "https://files.example/archive.zip",
    filename: "archive.zip"
  });
  await new Promise((resolveDelay) => setTimeout(resolveDelay, 0));
  assert.equal(harness.messages.length, 0);
  assert.equal(harness.pausedDownloads.size, 0);
  assert.equal(harness.cancelledDownloads.size, 0);
});

test("resumes the browser transfer when the user chooses Chrome", async () => {
  const harness = await createHarness();
  harness.interceptionDecision = "browser";
  harness.listeners.downloadCreated({
    id: 11,
    state: "in_progress",
    url: "https://files.example/archive.zip",
    filename: "archive.zip"
  });
  await new Promise((resolveDelay) => setTimeout(resolveDelay, 0));
  await new Promise((resolveDelay) => setTimeout(resolveDelay, 0));

  assert.equal(harness.pausedDownloads.has(11), false);
  assert.equal(harness.cancelledDownloads.has(11), false);
  assert.equal(harness.messages[0].request.type, "download.intercept");
  assert.equal(harness.messages.some(({ request }) => request.type === "download.handoffComplete"), false);
});

test("keeps the browser transfer when the desktop host cannot be reached", async () => {
  const harness = await createHarness();
  harness.failToConnect = true;
  harness.listeners.downloadCreated({
    id: 12,
    state: "in_progress",
    url: "https://files.example/archive.zip",
    filename: "archive.zip"
  });
  await new Promise((resolveDelay) => setTimeout(resolveDelay, 0));
  await new Promise((resolveDelay) => setTimeout(resolveDelay, 0));

  assert.equal(harness.pausedDownloads.has(12), false);
  assert.equal(harness.cancelledDownloads.has(12), false);
  assert.equal(harness.messages[0].request.type, "download.intercept");
});

test("removes the queued desktop copy and resumes Chrome if Chrome cannot be cancelled", async () => {
  const harness = await createHarness();
  harness.failToCancel = true;
  harness.listeners.downloadCreated({
    id: 13,
    state: "in_progress",
    url: "https://files.example/archive.zip",
    filename: "archive.zip"
  });
  await new Promise((resolveDelay) => setTimeout(resolveDelay, 0));
  await new Promise((resolveDelay) => setTimeout(resolveDelay, 0));

  assert.deepEqual(
    harness.messages.map(({ request }) => request.type),
    ["download.intercept", "download.removeQueued"]
  );
  assert.equal(harness.pausedDownloads.has(13), false);
  assert.equal(harness.cancelledDownloads.has(13), false);
});

test("context menus send explicit link and media URLs to the desktop", async () => {
  const { listeners, menus, messages } = await createHarness();
  listeners.installed();
  assert.deepEqual(menus.map((menu) => menu.contexts[0]), ["link", "video", "audio"]);
  listeners.menuClicked({
    menuItemId: "independent-download-link",
    linkUrl: "https://files.example/a.zip"
  });
  await new Promise((resolveDelay) => setTimeout(resolveDelay, 0));
  assert.equal(messages.length, 1);
  assert.equal(messages[0].request.type, "download.add");
  assert.equal(messages[0].request.url, "https://files.example/a.zip");
});

test("validates direct URLs before sending a native request", async () => {
  const harness = await createHarness();
  const started = await harness.send({
    type: "download:start",
    url: "https://files.example/archive.zip?token=private"
  });
  assert.equal(started.ok, true);
  assert.equal(harness.messages[0].request.type, "download.add");
  assert.equal(harness.messages[0].request.url, "https://files.example/archive.zip?token=private");

  const ftp = await harness.send({ type: "download:start", url: "ftp://files.example/archive.zip" });
  assert.equal(ftp.ok, false);
  assert.match(ftp.error, /HTTP and HTTPS/);

  const credentials = await harness.send({
    type: "download:start",
    url: "https://user:secret@files.example/archive.zip"
  });
  assert.equal(credentials.ok, false);
  assert.match(credentials.error, /credential-free/);
  assert.equal(harness.messages.length, 1);
});

test("lists desktop jobs without exposing their source URLs", async () => {
  const harness = await createHarness();
  const response = await harness.send({ type: "downloads:list" });
  assert.equal(response.ok, true);
  assert.equal(response.result[0].source, "files.example");
  assert.equal(response.result[0].fileName, "archive.zip");
  assert.equal("url" in response.result[0], false);
  assert.equal(harness.messages[0].request.type, "downloads.list");
});

test("routes controls through the native host and rejects malformed IDs", async () => {
  const harness = await createHarness();
  const id = harness.downloads[0].id;
  assert.equal((await harness.send({ type: "download:pause", id })).ok, true);
  assert.equal(harness.messages[0].request.type, "download.pause");
  assert.equal(harness.messages[0].request.id, id);

  const invalid = await harness.send({ type: "download:cancel", id: 42 });
  assert.equal(invalid.ok, false);
  assert.match(invalid.error, /not valid/);
  assert.equal(harness.messages.length, 1);
});

test("reports a missing native host without silently using Chrome Downloads", async () => {
  const harness = await createHarness();
  harness.failToConnect = true;
  const response = await harness.send({
    type: "download:start",
    url: "https://files.example/archive.zip"
  });
  assert.equal(response.ok, false);
  assert.match(response.error, /Could not connect to the desktop app/);
  assert.equal(harness.messages.length, 1);
});
