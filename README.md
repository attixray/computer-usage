# CompUsage

A local Windows work-log application based on the current user's lock and unlock events. C#/.NET 10 supplies the business logic and Windows integration; a WPF shell hosts the local WebView2 interface. No web server or account is needed.

## Features

- Up to 60 days of Security log events, with a local cache of previously read events.
- Configurable workday boundary (04:00), expected lunch (11:30), minimum lunch duration (15 minutes), and work-section split threshold (10 minutes).
- Automatic lunch suggestion, manual pair selection, no-lunch selection, and undo.
- Large, selectable dates/times; exact-second copy icons; individual section and whole-day copying as headerless Date/From/To TSV.
- Ctrl+C / Ctrl+Insert and Ctrl+V / Shift+Insert. Displayed text is minute-rounded; copy icons and Excel writing preserve original seconds.
- Hungarian and English UI; system, dark, and light themes.
- Settings dialog, remembered font/window size, expanded days, and scroll position.
- `.xlsx` Details preview, append, optional completion of an unfinished row, backup, and detection of changes after preview.

## Install and run

Run `CompUsage-1.0.0-Setup-x64.exe`, or extract the entire portable ZIP and run `CompUsage.exe`. Keep the portable files together. The .NET runtime is included. The installer adds the Microsoft WebView2 Runtime if missing; that step requires an internet connection. On Windows 11 it is normally already present.

The application runs as the current user. When Security-log permissions require it, Windows asks to elevate a narrowly scoped helper for that application session. Approve it using the same Windows account. The helper only reads that user's lock/unlock events and queries or changes the corresponding audit subcategory; the UI and workbook operations remain unelevated.

In Settings → Excel, select your `.xlsx` workbook. The `Details` worksheet must have Date, From, To, Duration, Duration (H), Details in columns A–F. Close the workbook in Excel before saving from CompUsage. An `.xls` file must first be saved as `.xlsx` in Excel.

## Time rules

The first observed unlock and last observed lock are shown for each workday. Lunch candidates must meet the minimum duration, last at most two hours, and begin within two hours of the expected lunch time; the closest start wins. A manual choice survives changes to those parameters.

Breaks strictly longer than the split threshold are excluded and split work rows. Shorter ordinary breaks count as work. The selected lunch break is always excluded, including a short manual lunch. Calculations use the original instants, not the rounded display values. Workday grouping uses the local Windows time zone and handles daylight-saving boundaries.

Current days and visibly incomplete or ambiguous event sequences are excluded from automatic append. A current day's finished sections can still be copied individually. Multiple Windows sessions are marked for manual review. Windows cannot recover events from before auditing was enabled or after they were lost from the log; missing events cannot always be detected just from an alternating sequence. These are observed computer-unlock intervals, not proof of working time.

## Excel behavior

Preview proposes only complete days later than the last date already present. A matching unfinished row on that last date is offered separately; its existing start is preserved. Starts matching the exact or minute-rounded unlock are recognized. Completion ends at the matching work section's end, not across an excluded break. Further sections on an already-present date can be copied manually.

New rows contain numeric dates/times and durations; their Details text stays blank. Date formatting is `yyyy.mm.dd`, displayed time formatting is `hh:mm`. Other worksheets and unrelated workbook parts are preserved. Existing unfinished-row duration formulas are retained. The workbook is marked for recalculation by Excel. Both 1900 and 1904 date systems are supported. Plain worksheet ranges are the supported target; expanding structured Excel tables is not implemented.

Before replacement, the app checks the file's content hash against the preview and requires exclusive access. A backup named `<workbook>.backup-<timestamp>-<id>.xlsx` is created beside the original by Windows' atomic file-replacement operation. Shares that do not support that operation fail without a fallback overwrite.

## Local data

Preferences, manual lunch choices, cached events, and window state are stored in `%LocalAppData%\CompUsage\state.json`. The WebView2 profile lives in the same application data folder. Uninstall keeps this data and all workbooks. No event log, workbook, or user configuration belongs in the Git repository.

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

## Structure

- `src/CompUsage.Core`: event parsing, time rules, work sections, workbook preview/write.
- `src/CompUsage.App`: WPF host, restricted native bridge, elevation helper, local state, and local UI.
- `tests/CompUsage.Checks`: deterministic time and workbook checks using generated fixtures.
- `tests/ui-checks.cjs`: DOM-based interaction and localization checks, without launching a browser.
- `installer`: NSIS installer and uninstall definition.

The release binaries are currently unsigned. Third-party components retain their own licenses; see `THIRD-PARTY-NOTICES.md`.
