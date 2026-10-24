# Independent Download Helper for Chrome

This is a new, independently written Manifest V3 extension. It does not
contain IDM code, branding, icons, or copied extension assets.

## What it does

- Accepts a user-entered direct HTTP(S) URL and adds it to the Windows desktop
  download manager.
- Adds explicit right-click actions for links, video elements, and audio
  elements.
- Scans the active tab for direct HTTP(S) `<video>`, `<audio>`, and `<source>`
  URLs only after the user presses **Scan this tab** in the popup.
- Lists desktop downloads and exposes pause, resume, cancel, open, and
  show-in-folder actions.
- By default, watches new Chrome HTTP(S) downloads, pauses them, and prompts
  whether to continue in the desktop manager or leave them in Chrome. This can
  be disabled in the extension popup.
- Uses a Chrome Native Messaging host and a same-user Windows named pipe to
  connect to the desktop app; it does not create an unauthenticated localhost
  web server.

The media scan is user-triggered, reads only the media element source URLs,
and does not run on every page. Automatic download handoff uses Chrome's
`downloads.onCreated` event: Chrome starts the transfer, the extension pauses
it as soon as notified, and a desktop prompt lets the user choose the handler.
Chrome's API does not provide a pre-request blocking hook in this extension
model, so very fast downloads may complete before the pause takes effect.
Browser-saved cookies, POST bodies, and authentication headers are not
transferred to the desktop downloader; downloads requiring those may fail.

Segmented HLS/DASH manifests are identified as unsupported rather than being
presented as complete video downloads. This extension does not extract
protected/DRM media or reconstruct site-specific streaming formats. Downloading
such streams requires a separate permitted implementation and testing path.

## Build and register the native host

Publish both the desktop app and native host side-by-side as described in the
top-level README. From this folder, run the installer script in PowerShell:

```powershell
.\install-native-host.ps1
```

The script registers the current user's Chrome Native Messaging host and
restricts access to this extension's fixed ID. If the executable is outside
the default build/publish locations, pass its full path:

```powershell
.\install-native-host.ps1 -NativeHostPath "C:\path\to\NativeMessagingHost.exe"
```

To remove the registration:

```powershell
.\install-native-host.ps1 -Uninstall
```

## Load unpacked

1. Open `chrome://extensions` in Chrome.
2. Enable **Developer mode**.
3. Select **Load unpacked** and choose this `chrome-extension` folder.
4. Confirm the extension ID is `nbadibcelhhpkkdjlocddlhgdbcbappn`, then pin
   **Independent Download Helper** if you want quick popup access.
5. Reload the extension if Chrome asks, and approve the new **downloads** and
   **storage** permissions.

Restart Chrome after registering the native host. The first extension request
starts the desktop app if it is not already running. If a handoff fails before
the Chrome item is cancelled, the extension resumes the original browser
transfer and shows an error badge instead of starting a second download. If
the final desktop queue confirmation fails after Chrome has been cancelled,
the pending item remains visible in the app; use **Start Queue** there.

If the official IDM browser extension is still enabled, it may also pause the
same browser download. Disable IDM's interception while testing this extension
to prevent both download managers from handling one item.

## Permissions

- `activeTab` and `scripting`: one-time, user-triggered media URL scan of the
  active page.
- `contextMenus`: explicit right-click download actions on links/media.
- `downloads`: observe, pause, resume, and cancel new browser downloads for
  the optional desktop handoff.
- `nativeMessaging`: communicate with the installed desktop app.
- `storage`: retain the browser-interception preference.
