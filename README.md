# Gemini Launcher

A lightweight replacement for the Windows Start menu and search (Bing). Tap the **Windows key** or click **Start** on the taskbar, start typing, and you get:

- **Google results right in the launcher**: the real Google results page shows up in the preview pane as you type, just like google.com. Press **Enter** for a full-size page. Clicking a result opens it in your default browser.
- **Google autocomplete** as you type, with inline ghost completion (press **Tab** or **→** to accept)
- **Gemini answers** grounded in **Google Search**, with the **URL context** tool turned on. Paste a link and Gemini reads and summarizes the page.
- **Apps**: every app Windows knows about (desktop and Microsoft Store), with real icons
- **Files and documents**: instant results from the Windows Search index, matching file names and the words inside documents
- **Settings pages** (Wi-Fi, Bluetooth, Display…), a **calculator**, and **website** shortcuts

When you open it without typing, you see:

- **Pinned** apps, files, folders and websites. Right-click anything to pin it, or use **Add**. **Drag a pin** to rearrange your pins, or right-click it and pick **Move to front / left / right / end**.
- **Recommended**: the apps you use most (from Windows' own usage data) and your recent files. Hover over an item and click **×** (or press **Del**, or right-click → **Remove from list**) to remove it.
- **Searches**: your recent searches plus trending Google searches for your region. Remove any of them the same way. Removed items stay hidden; **Settings → Data → Restore removed suggestions** brings them back.

When you type, apps, settings and files are listed first and web results (Google, Gemini and Google suggestions) come after them. Clicking anywhere outside the launcher closes it, just like Start.

## Google results

When there's no app or file match, the **Google** row is selected and the preview pane shows Google's own results page for what you typed (it waits for a short pause in typing, so it doesn't load a page on every key press).

- In the preview pane, Google uses its phone-sized layout. Press **Enter** (or **Ctrl+E**, or the expand button) to switch to the **full-size page**: the launcher grows and Google shows its regular desktop layout. **Esc** goes back to the list. Press **Enter** again to open the search in your browser.
- **Clicking a result** or any link that leaves Google opens it in your **default browser** and closes the launcher. Google's tabs (Images, News…), the next page of results, and new searches typed into Google's search box stay inside.
- Selecting a Google suggestion shows its results. The **Gemini** row is right below the Google row, and Gemini answers work as before.
- Prefer Gemini answers while you type? **Settings → Search & recommendations → When you search the web, show → Gemini answer**. The Google row is still one row below.

It uses the **Microsoft Edge WebView2 Runtime**, which is built into Windows 11 and most Windows 10 PCs. If it's missing, the launcher works as before: Gemini answers come first, and the Google row opens Google in your browser.

### No WebView2? (for example, you removed Microsoft Edge)

The WebView2 Runtime is separate from the Edge browser. Installing it doesn't bring Edge back, and it doesn't show up as a browser. Two ways to get Google results in the launcher:

1. **Install it** (updates itself): get the *Evergreen Bootstrapper* from [Microsoft's WebView2 page](https://developer.microsoft.com/microsoft-edge/webview2/), then reopen the launcher.
2. **Portable, no installer**: on the same page, download the *Fixed Version* package for your CPU (x64 for most PCs), then extract it in a terminal:
   ```
   expand Microsoft.WebView2.FixedVersionRuntime.<version>.x64.cab -F:* "%LOCALAPPDATA%\GeminiLauncher\WebView2Runtime"
   ```
   The launcher finds it there automatically and sets the folder permissions it needs. This copy doesn't update itself, so replace it with a newer version now and then for security fixes. The browser starts when you begin typing and shuts down after the launcher has been closed for 5 minutes, so it doesn't use memory while idle. Its cookies (such as Google's consent choice) are kept in `%LOCALAPPDATA%\GeminiLauncher\WebView2`.

It looks like Windows 11: it slides up from behind the taskbar like Start, with a system acrylic backdrop, rounded corners, Segoe UI Variable, your accent color, and it switches live between light and dark mode. On Windows 10 it uses acrylic blur. If transparency effects are off in Windows, it uses solid colors.

## Install

1. Download `GeminiLauncher.exe`. It's a single ~750 KB file and needs nothing else (it uses .NET Framework 4.8, which is built into Windows 10/11).
2. Run it and click **Install**. It installs for your account only, so no admin rights are needed. It goes to `%LOCALAPPDATA%\Programs\GeminiLauncher`, adds a Start menu entry, registers in **Settings → Apps** for uninstalling, and starts with Windows.
   - Windows SmartScreen may warn you because the file isn't code-signed. Click **More info → Run anyway**.
3. Paste your Gemini API key in Settings. You can get a free one at [Google AI Studio](https://aistudio.google.com/apikey). The key is encrypted with Windows DPAPI, so only your Windows account can read it.

To run without installing, use `GeminiLauncher.exe --portable`.

## Replacing the Start menu (optional, on by default)

In **Settings → Replace the Start menu** (also in the tray icon menu):

| Switch | What it does |
|---|---|
| **Windows key opens this launcher** | A lone tap of the Windows key opens Gemini Launcher instead of Start. Win+E, Win+D, Win+Shift+S and other shortcuts keep working. |
| **Taskbar Start button opens this launcher** | Clicking the Windows logo on the taskbar opens Gemini Launcher instead of Start (all monitors). |

You can still open the **native Start menu** at any time: press **Ctrl+Esc**, **Shift+click** the Start button, or use the tray menu → *Open native Start menu*. Turn both switches off to get the native behavior back completely. Nothing in Windows is modified or patched, and closing the app restores everything.

**Admin apps:** Windows doesn't let normal apps see keys you press while an app running as administrator is focused, such as Task Manager. If you want the Windows key to work there too, turn on **Also work over admin apps**. It creates a sign-in task with admin rights, after one UAC prompt. Apps you launch from the launcher still run with normal rights.

## Keyboard

| Key | Action |
|---|---|
| Type | Search everything |
| ↑ / ↓, PgUp / PgDn | Move selection |
| Enter | Open the selected item. On the Google row it opens the full-size Google page (Enter again opens it in your browser). On a suggestion it shows its Google results (or asks Gemini if Gemini is your web preview). On Gemini it asks Gemini, and Enter again on a finished answer opens Google. |
| Ctrl+E | Switch between the preview and the full-size Google page |
| Tab / → | Accept the autocomplete |
| Ctrl+Enter | Open the query in Google in your browser |
| Ctrl+Shift+Enter | Run as administrator |
| Alt+Enter | Open file location |
| Ctrl+P | Pin or unpin the selected item |
| Alt+Shift+← / → | Move the focused pin (same as Windows 11 Start) |
| Del | Remove the focused suggestion, or unpin the focused pin |
| Shift+Del | Remove the selected Google suggestion while typing |
| Menu key / right-click | More actions |
| Ctrl+, | Settings |
| Esc | Inside the Google page: back to the search box. Full-size page: back to the list. Otherwise: clear, then close |

## Gemini settings

- **Model**: `gemini-3.5-flash-lite` by default, because it's the fastest. `gemini-3.8-flash` gives richer answers. You can type any model name.
- **Thinking level**: Medium by default (Minimal / Low / Medium / High / Model default). On Gemini 2.5 models it's converted to a thinking budget.
- **Grounding tools**: Google Search, Google Maps and URL context are all on and sent together. If a model rejects a combination, the launcher retries without the conflicting tool, and the **Test** button tells you exactly what your model accepted.
  - Google Maps places appear as chips labelled with the place name and "Google Maps", linking to the place.
  - **Use my location for Google Maps** is off by default. Turn it on to send your approximate location (about 100 m) with requests, so "near me" questions work.
- **Safety filters**: off. Every adjustable category (harassment, hate speech, sexually explicit, dangerous content, jailbreak) is set to `OFF`. Google's built-in protections still apply.
- **Instant answers while typing**: when you pause typing and no app or file is an obvious match, Gemini answers automatically. Turn this off if you only want to ask when you press Enter. Google Search and Google Maps grounding each include 5,000 free queries per month on Gemini 3+ models; after that, Google bills them.

## Lightweight

- Single small exe, with no runtime to install, no background indexing (it uses the Windows Search index) and no browser engine
- Keyboard and mouse hooks run on their own thread, so the app can never slow down your typing
- Memory is trimmed after the launcher has been hidden for 20 seconds

## Uninstall

Go to **Settings → Apps → Installed apps → Gemini Launcher → Uninstall**, or run `GeminiLauncher.exe --uninstall`.

## Build from source

```
dotnet build src/GeminiLauncher.csproj -c Release
```

This works on Windows, macOS or Linux (it targets `net48` with `EnableWindowsTargeting`).
