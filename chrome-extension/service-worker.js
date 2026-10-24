const NATIVE_HOST = "com.independentdownloadmanager.bridge";
const INTERCEPTION_ENABLED_KEY = "browserInterceptionEnabled";
const MENU_LINK = "independent-download-link";
const MENU_VIDEO = "independent-download-video";
const MENU_AUDIO = "independent-download-audio";
const interceptedDownloadIds = new Set();

chrome.downloads.onCreated.addListener((item) => {
  void interceptBrowserDownload(item);
});

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: MENU_LINK,
      title: "Add link to desktop download manager",
      contexts: ["link"]
    });
    chrome.contextMenus.create({
      id: MENU_VIDEO,
      title: "Add video file to desktop download manager",
      contexts: ["video"]
    });
    chrome.contextMenus.create({
      id: MENU_AUDIO,
      title: "Add audio file to desktop download manager",
      contexts: ["audio"]
    });
  });
});

chrome.contextMenus.onClicked.addListener((info) => {
  const url = info.menuItemId === MENU_LINK
    ? info.linkUrl
    : info.menuItemId === MENU_VIDEO || info.menuItemId === MENU_AUDIO
      ? info.srcUrl
      : null;

  if (url) {
    void sendToDesktop("download.add", { url })
      .catch((error) => console.error("Could not add desktop download:", error));
  }
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  handleMessage(message)
    .then((result) => sendResponse({ ok: true, result }))
    .catch((error) => sendResponse({ ok: false, error: error.message }));
  return true;
});

async function handleMessage(message) {
  switch (message?.type) {
    case "download:start":
      return await sendToDesktop("download.add", { url: validateDownloadUrl(message.url) });
    case "downloads:list":
      return await sendToDesktop("downloads.list");
    case "desktop:status":
      return await sendToDesktop("desktop.status");
    case "interception:get":
      return await getInterceptionEnabled();
    case "interception:set":
      return await setInterceptionEnabled(message.enabled);
    case "download:pause":
    case "download:resume":
    case "download:cancel":
    case "download:open":
    case "download:show":
      return await sendToDesktop(message.type.replace(":", "."), {
        id: requireDownloadId(message.id)
      });
    default:
      throw new Error("Unsupported extension request.");
  }
}

async function getInterceptionEnabled() {
  const stored = await chrome.storage.local.get(INTERCEPTION_ENABLED_KEY);
  return stored[INTERCEPTION_ENABLED_KEY] !== false;
}

async function setInterceptionEnabled(enabled) {
  if (typeof enabled !== "boolean") {
    throw new Error("Choose whether browser download interception is enabled.");
  }
  await chrome.storage.local.set({ [INTERCEPTION_ENABLED_KEY]: enabled });
  return { enabled };
}

async function interceptBrowserDownload(item) {
  if (!item || !Number.isInteger(item.id) || item.state !== "in_progress" ||
      item.paused || interceptedDownloadIds.has(item.id)) {
    return;
  }

  let url;
  try {
    url = validateDownloadUrl(item.finalUrl || item.url);
  } catch {
    return;
  }

  try {
    if (!(await getInterceptionEnabled())) {
      return;
    }
  } catch (error) {
    await showInterceptionError(error);
    return;
  }

  interceptedDownloadIds.add(item.id);
  let isPaused = false;
  try {
    await chrome.downloads.pause(item.id);
    isPaused = true;

    const parsedUrl = new URL(url);
    const fileName = getSuggestedFileName(item.filename, parsedUrl);
    const handoff = await sendToDesktop("download.intercept", {
      url,
      fileName,
      source: parsedUrl.hostname
    });

    if (handoff?.decision !== "desktop" || typeof handoff.id !== "string") {
      await chrome.downloads.resume(item.id);
      isPaused = false;
      return;
    }

    try {
      await chrome.downloads.cancel(item.id);
      isPaused = false;
    } catch (error) {
      await sendToDesktop("download.removeQueued", { id: handoff.id });
      await chrome.downloads.resume(item.id);
      isPaused = false;
      throw new Error(`Chrome kept the download, so it was not transferred to the desktop app: ${error.message}`);
    }

    await completeDesktopHandoff(handoff.id);
    await clearInterceptionError();
  } catch (error) {
    if (isPaused) {
      try {
        await chrome.downloads.resume(item.id);
      } catch (resumeError) {
        console.error("Could not resume the browser download:", resumeError);
      }
    }
    await showInterceptionError(error);
  } finally {
    interceptedDownloadIds.delete(item.id);
  }
}

async function completeDesktopHandoff(id) {
  let lastError;
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      await sendToDesktop("download.handoffComplete", { id });
      return;
    } catch (error) {
      lastError = error;
      if (attempt === 0) {
        await new Promise((resolveDelay) => setTimeout(resolveDelay, 250));
      }
    }
  }
  throw lastError;
}

function getSuggestedFileName(filename, url) {
  const fromBrowser = typeof filename === "string"
    ? filename.split(/[\\/]/).pop()
    : "";
  if (fromBrowser) {
    return fromBrowser.slice(0, 255);
  }

  const pathName = url.pathname.split("/").filter(Boolean).pop();
  if (!pathName) {
    return "download";
  }
  try {
    return decodeURIComponent(pathName).slice(0, 255);
  } catch {
    return pathName.slice(0, 255);
  }
}

async function showInterceptionError(error) {
  console.error("Could not hand the browser download to the desktop app:", error);
  await chrome.action.setBadgeBackgroundColor({ color: "#b42318" });
  await chrome.action.setBadgeText({ text: "!" });
  await chrome.action.setTitle({ title: "Download Helper: download handoff needs attention" });
}

async function clearInterceptionError() {
  await chrome.action.setBadgeText({ text: "" });
  await chrome.action.setTitle({ title: "Independent Download Helper" });
}

function validateDownloadUrl(value) {
  if (typeof value !== "string" || value.length > 8192) {
    throw new Error("Enter a valid HTTP or HTTPS URL.");
  }

  let url;
  try {
    url = new URL(value);
  } catch {
    throw new Error("Enter a valid HTTP or HTTPS URL.");
  }

  if ((url.protocol !== "http:" && url.protocol !== "https:") ||
      url.username || url.password) {
    throw new Error("Only credential-free HTTP and HTTPS URLs are supported.");
  }

  return url.href;
}

function requireDownloadId(value) {
  if (typeof value !== "string" ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) {
    throw new Error("The selected desktop download is not valid.");
  }
  return value;
}

async function sendToDesktop(type, data = {}) {
  let response;
  try {
    response = await chrome.runtime.sendNativeMessage(NATIVE_HOST, {
      requestId: crypto.randomUUID(),
      type,
      ...data
    });
  } catch {
    throw new Error("Could not connect to the desktop app. Run the installer or open the app and try again.");
  }

  if (!response?.ok) {
    throw new Error(response?.error || "The desktop app could not complete that action.");
  }
  return response.result;
}
