const form = document.querySelector("#download-form");
const urlInput = document.querySelector("#url");
const scanButton = document.querySelector("#scan-media");
const refreshButton = document.querySelector("#refresh");
const mediaList = document.querySelector("#media-list");
const downloadsList = document.querySelector("#downloads-list");
const scanNote = document.querySelector("#scan-note");
const message = document.querySelector("#message");
const interceptionToggle = document.querySelector("#intercept-browser");

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  await startDownload(urlInput.value);
});

scanButton.addEventListener("click", scanActiveTab);
refreshButton.addEventListener("click", refreshDownloads);
interceptionToggle.addEventListener("change", saveInterceptionSetting);

void loadInterceptionSetting();
void refreshDownloads();

async function loadInterceptionSetting() {
  try {
    interceptionToggle.checked = await sendRequest("interception:get");
  } catch (error) {
    interceptionToggle.disabled = true;
    showMessage(error.message);
  }
}

async function saveInterceptionSetting() {
  interceptionToggle.disabled = true;
  try {
    const result = await sendRequest("interception:set", {
      enabled: interceptionToggle.checked
    });
    showMessage(
      result.enabled
        ? "Chrome downloads will be offered to the desktop manager."
        : "Chrome downloads will stay in Chrome.",
      true
    );
  } catch (error) {
    interceptionToggle.checked = !interceptionToggle.checked;
    showMessage(error.message);
  } finally {
    interceptionToggle.disabled = false;
  }
}

async function sendRequest(type, data = {}) {
  const response = await chrome.runtime.sendMessage({ type, ...data });
  if (!response?.ok) {
    throw new Error(response?.error || "The extension could not complete that action.");
  }
  return response.result;
}

async function startDownload(url) {
  clearMessage();
  try {
    await sendRequest("download:start", { url });
    urlInput.value = "";
    showMessage("Download added to the desktop manager.", true);
    await refreshDownloads();
  } catch (error) {
    showMessage(error.message);
  }
}

async function scanActiveTab() {
  clearMessage();
  mediaList.replaceChildren();
  scanButton.disabled = true;
  scanNote.textContent = "Checking media sources in the active tab…";

  try {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (!tab?.id) {
      throw new Error("No active tab is available.");
    }

    const results = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: findDirectMedia
    });
    const candidates = results.flatMap((result) => result.result || []);

    if (candidates.length === 0) {
      scanNote.textContent = "No direct HTTP(S) video or audio file sources were found.";
      return;
    }

    scanNote.textContent = `Found ${candidates.length} media source(s). Segmented playlists are not downloaded as complete videos.`;
    for (const candidate of candidates) {
      mediaList.append(createMediaItem(candidate));
    }
  } catch (error) {
    scanNote.textContent = "The active page did not allow a media scan.";
    showMessage(error.message);
  } finally {
    scanButton.disabled = false;
  }
}

function findDirectMedia() {
  const results = new Map();
  for (const element of document.querySelectorAll("video, audio, source")) {
    const urls = [];
    if (element instanceof HTMLMediaElement) {
      urls.push(element.currentSrc, element.src);
    } else if (element instanceof HTMLSourceElement) {
      urls.push(element.src);
    }

    for (const value of urls) {
      if (!value) continue;
      try {
        const url = new URL(value, document.baseURI);
        if (url.protocol !== "http:" && url.protocol !== "https:") continue;
        results.set(url.href, {
          url: url.href,
          host: url.hostname,
          kind: element.localName
        });
      } catch {
        // Ignore malformed or browser-internal media URLs.
      }
    }
  }
  return [...results.values()].slice(0, 30);
}

function createMediaItem(candidate) {
  const item = document.createElement("li");
  item.className = "media-item";
  const top = document.createElement("div");
  top.className = "item-top";
  const labels = document.createElement("div");
  labels.className = "labels";
  const name = document.createElement("div");
  name.className = "item-name";
  name.textContent = mediaName(candidate.url);
  const meta = document.createElement("div");
  meta.className = "item-meta";
  meta.textContent = `${candidate.kind} · ${candidate.host}`;
  labels.append(name, meta);
  top.append(labels);
  item.append(top);

  const segmented = isSegmentedMedia(candidate.url);
  const addButton = document.createElement("button");
  addButton.type = "button";
  addButton.textContent = segmented ? "Unsupported stream" : "Download";
  addButton.disabled = segmented;
  addButton.addEventListener("click", () => {
    addButton.disabled = true;
    void startDownload(candidate.url).finally(() => {
      if (item.isConnected) addButton.disabled = false;
    });
  });
  const actions = document.createElement("div");
  actions.className = "item-actions";
  actions.append(addButton);
  item.append(actions);

  if (segmented) {
    const warning = document.createElement("p");
    warning.className = "item-warning";
    warning.textContent = "This is a stream manifest, not a complete media file. Stream merging and protected-media extraction are not supported.";
    item.append(warning);
  }
  return item;
}

function isSegmentedMedia(value) {
  try {
    return /\.(?:m3u8?|mpd)(?:$|[?#])/i.test(new URL(value).pathname);
  } catch {
    return true;
  }
}

function mediaName(value) {
  try {
    const path = new URL(value).pathname;
    const segment = path.split("/").filter(Boolean).pop();
    if (!segment) return "Media file";
    try {
      return decodeURIComponent(segment);
    } catch {
      return segment;
    }
  } catch {
    return "Media file";
  }
}

async function refreshDownloads() {
  try {
    const downloads = await sendRequest("downloads:list");
    renderDownloads(downloads);
  } catch (error) {
    downloadsList.replaceChildren(createEmptyItem("Could not load desktop downloads."));
    showMessage(error.message);
  }
}

function renderDownloads(downloads) {
  downloadsList.replaceChildren();
  if (downloads.length === 0) {
    downloadsList.append(createEmptyItem("Downloads added from this extension will appear here."));
    return;
  }

  for (const download of downloads) {
    downloadsList.append(createDownloadItem(download));
  }
}

function createDownloadItem(download) {
  const item = document.createElement("li");
  item.className = "download-item";
  const top = document.createElement("div");
  top.className = "item-top";
  const labels = document.createElement("div");
  labels.className = "labels";
  const name = document.createElement("div");
  name.className = "item-name";
  name.textContent = download.fileName;
  name.title = download.fileName;
  const meta = document.createElement("div");
  meta.className = "item-meta";
  meta.textContent = `${download.source} · ${download.status}`;
  labels.append(name, meta);
  top.append(labels);
  item.append(top);

  if (download.status === "Downloading" || download.status === "Queued" ||
      download.status === "Paused" || download.status === "Stopping") {
    const progress = document.createElement("div");
    progress.className = "item-meta";
    progress.textContent = `${download.progress} · ${download.rate} · ${download.timeLeft}`;
    item.append(progress);
  }

  const actions = document.createElement("div");
  actions.className = "item-actions";
  if (download.status === "Downloading") {
    actions.append(createActionButton("Pause", "download:pause", download.id));
  }
  if (["Paused", "Failed", "Cancelled"].includes(download.status)) {
    actions.append(createActionButton("Resume", "download:resume", download.id));
  }
  if (["Queued", "Downloading", "Pausing", "Paused"].includes(download.status)) {
    actions.append(createActionButton("Cancel", "download:cancel", download.id));
  }
  if (download.status === "Complete") {
    actions.append(createActionButton("Open", "download:open", download.id));
    actions.append(createActionButton("Show in folder", "download:show", download.id));
  }
  if (actions.childElementCount) item.append(actions);
  return item;
}

function createActionButton(title, type, id) {
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = title;
  button.addEventListener("click", async () => {
    button.disabled = true;
    try {
      await sendRequest(type, { id });
      await refreshDownloads();
    } catch (error) {
      button.disabled = false;
      showMessage(error.message);
    }
  });
  return button;
}

function createEmptyItem(text) {
  const item = document.createElement("li");
  item.className = "empty";
  item.textContent = text;
  return item;
}

function showMessage(text, success = false) {
  message.textContent = text;
  message.classList.toggle("success", success);
}

function clearMessage() {
  message.textContent = "";
  message.classList.remove("success");
}
