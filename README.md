# CompUsage

A local Windows work-log application based on the current user's lock and unlock events. C#/.NET 10 supplies the business logic and Windows integration; a WPF shell hosts the local WebView2 interface. No web server or account is needed.

## Features

- Up to 60 days of Security log events, with a local cache of previously read events.
- Configurable workday boundary (04:00), expected lunch (11:30), minimum lunch duration (15 minutes), and work-section split threshold (10 minutes).
- Automatic lunch suggestion, manual pair selection, no-lunch selection, and undo.
- Large, selectable dates/times; exact-second copy icons; individual section and whole-day copying as headerless Date/From/To TSV.
- Ctrl+C / Ctrl+Insert and Ctrl+V / Shift+Insert. Display follows the workbook's date/time formats and Windows regional separators. Hidden seconds are not rounded; copy icons and Excel writing preserve original seconds.
- Hungarian and English UI; system, dark, and light themes.
- A prominent disabled-logging warning opens the logging settings directly.
- Settings dialog, remembered font/window size, expanded days, and scroll position.
- Optional background last-input tracking, disabled by default, with selectable lock-time or last-input endpoints for display, copying and Excel.
- `.xlsx` Details preview, append, optional completion of an unfinished row, backup, and detection of changes after preview.

## Install and run

Run `CompUsage-1.0.3-Setup-x64.exe`, or extract the entire portable ZIP and run `CompUsage.exe`. Keep the portable files together. The .NET runtime is included. The installer adds the Microsoft WebView2 Runtime if missing; that step requires an internet connection. On Windows 11 it is normally already present.

Setup registers a per-user Windows sign-in entry for the background collector and invokes it independently of the optional UI launch. Tracking defaults to disabled, so the collector exits without sampling until enabled in Settings → Time rules. Once enabled, it runs immediately, survives closing the window and starts again at subsequent sign-ins without opening the app. Disabling tracking stops it; the startup entry remains but exits immediately while disabled. Upgrade and uninstall stop the collector before replacing/removing its files; uninstall removes the startup entry and keeps saved data. The portable build can collect after enabling it in Settings, but Setup is needed to register automatic startup.

The application runs as the current user. When Security-log permissions require it, Windows asks to elevate a narrowly scoped helper for that application session. Approve it using the same Windows account. The helper only reads that user's lock/unlock events and queries or changes the corresponding audit subcategory; the UI and workbook operations remain unelevated.

In Settings → Excel, select your `.xlsx` workbook. The `Details` worksheet must have Date, From, To, Duration, Duration (H), Details in columns A–F. Close the workbook in Excel before saving from CompUsage. An `.xls` file must first be saved as `.xlsx` in Excel.

## Time rules

The first observed unlock and last observed lock are shown for each workday. Lunch candidates must meet the minimum duration, last at most two hours, and begin within two hours of the expected lunch time; the closest start wins. A manual choice survives changes to those parameters.

Breaks strictly longer than the split threshold are excluded and split work rows. Shorter ordinary breaks count as work. The selected lunch break is always excluded, including a short manual lunch. Calculations use the original instants, not formatted display text. Workday grouping uses the local Windows time zone and handles daylight-saving boundaries.

Current days and visibly incomplete or ambiguous event sequences are excluded from automatic append. A current day's finished sections can still be copied individually. Multiple Windows sessions are marked for manual review. Windows cannot recover events from before auditing was enabled or after they were lost from the log; missing events cannot always be detected just from an alternating sequence. These are observed computer-unlock intervals, not proof of working time.

## Excel behavior

Preview proposes only complete days later than the last date already present. A matching unfinished row on that last date is offered separately; its existing start is preserved. Exact starts and starts entered using the displayed minute are recognized, including older rounded entries. Completion ends at the matching work section's end, not across an excluded break. Further sections on an already-present date can be copied manually.

New rows contain numeric dates/times and durations; their Details text stays blank. Existing cell, row and column number formats are retained or inherited. The UI reads the Details date, From and To formats instead of forcing its own format. For example, `h:mm` displays `8:01:37` as `8:01`, while `hh:mm:ss` displays `08:01:37`. Regional separators and literal quoted separators are handled separately. Without an available workbook, the fallback is regional year/month/day and 24-hour time without a leading zero.

Other worksheets and unrelated workbook parts are preserved. Existing unfinished-row duration formulas are retained. The workbook is marked for recalculation by Excel. Both 1900 and 1904 date systems are supported. Plain worksheet ranges are the supported target; expanding structured Excel tables is not implemented.

Before replacement, the app checks the file's content hash against the preview and requires exclusive access. A backup named `<workbook>.backup-<timestamp>-<id>.xlsx` is created beside the original by Windows' atomic file-replacement operation. Shares that do not support that operation fail without a fallback overwrite.

## Optional activity tracking

The unelevated `--activity-monitor` process samples the current Windows session's `GetLastInputInfo` about once per second while unlocked. On a Windows session-lock notification it saves the latest sample. It never captures keys, text, application names or screenshots, and never starts the elevated event-log helper. The main window shows collector health and automatic-start registration in Time rules; lock details show the last observed input and idle duration. A stopped collector can be restarted from the status row.

The time-source setting defaults to Windows lock time. Selecting last input changes lock endpoints consistently across the UI, lunch suggestion, break splitting, TSV copying and automatic Details append. Original Windows event timestamps and manual lunch-pair identities remain intact; the raw event list still shows the Windows times. Existing Excel rows are not rewritten when the mode changes. A missing, stale, ambiguous, different-session or invalid observation falls back to the original lock time. An input cannot move a lock before the preceding unlock or into a different workday. Both modes preserve source seconds.

This is an input-time estimate, not a guaranteed manual/automatic-lock classifier or proof of work. Win+L itself is input, and Windows can count software-generated input too. Samples from before tracking was enabled cannot be reconstructed. Collector history covers the 60-day UI range with a one-day retention margin for workday boundaries. No input is sampled while locked, disconnected or disabled.

## Local data

Preferences, manual lunch choices, cached events, and window state are stored in `%LocalAppData%\CompUsage\state.json`. The WebView2 profile lives in the same application data folder. Uninstall keeps this data and all workbooks. No event log, workbook, or user configuration belongs in the Git repository.

The optional monitor uses `%LocalAppData%\CompUsage\Activity\configuration.json`, separate session journals and small heartbeat files. The UI never writes the monitor's history, and the monitor never writes UI state. These files stay local and are retained on uninstall.

## Build

Prerequisites: Windows x64, .NET SDK 10.0.402, Node.js 22 or later, and PowerShell 7. Source comments and development documentation are in English.

```powershell
./build.ps1
```

The build restores packages, runs C# and UI checks, publishes a self-contained application, verifies the downloaded NSIS compiler by its pinned SHA-256 checksum, verifies Microsoft's signature on the WebView2 bootstrapper, and creates Setup and portable ZIP files under `artifacts/release/`. The bootstrapper URL follows Microsoft's Evergreen release; the compiler and SDK versions are pinned.

If local policy prevents scripts, run the commands in `build.ps1` from an allowed shell or use the GitHub Actions workflow. There is no need to weaken machine execution policy.

Useful development commands:

```powershell
dotnet run --project tests/CompUsage.Checks -c Release
npm ci
npm test
dotnet run --project src/CompUsage.App
```

`CompUsage.exe --verify-install` checks the deployed UI assets and installed WebView2 runtime without opening a window or modifying Windows auditing. Exit 0 means those prerequisites were found; this is not an interactive UI test.

`CompUsage.exe --verify-broker` starts an unelevated helper and checks the authenticated, same-user pipe round trip. It does not read the event log, modify auditing, or display UAC. A real UAC transition still needs an interactive Windows check.

`CompUsage.exe --verify-activity-monitor` checks disabled-by-default startup, an isolated collector process and heartbeat, duplicate prevention, a synthetic lock notification, graceful stop, and disabled subsequent startup. It uses a temporary journal, does not register startup, change auditing or lock the desktop, and cleans up after itself. The normal build runs it after publishing. A physical lock/unlock cycle and Windows sign-in remain manual end-to-end checks.

The build also runs `tests/installer-monitor-check.ps1`: it compiles the actual NSIS stop macro with a private scope and verifies graceful shutdown of an isolated background collector. It does not install the app or change startup registration.

## Structure

- `src/CompUsage.Core`: event parsing, time rules, work sections, workbook preview/write.
- `src/CompUsage.App`: WPF host, restricted native bridge, elevation helper, local state, and local UI.
- `tests/CompUsage.Checks`: deterministic time and workbook checks using generated fixtures.
- `tests/ui-checks.cjs`: DOM-based interaction and localization checks, without launching a browser.
- `installer`: NSIS installer and uninstall definition.

The release binaries are currently unsigned. Third-party components retain their own licenses; see `THIRD-PARTY-NOTICES.md`.
