# CompUsage

A local Windows work-log application based on the current user's lock and unlock events. C#/.NET 10 supplies the business logic and Windows integration; a WPF shell hosts the local WebView2 interface. No web server or account is needed.

## Features

- Up to 60 days of Security log events, with a local cache of previously read events. Session reconnect/disconnect events (4778/4779) cover RDP and console switches that do not produce a lock/unlock (4800/4801). Sign-in (4624) and sign-out (4647) events cover days that start after boot or end with a shutdown or restart.
- Configurable workday boundary (04:00), expected lunch (11:30), minimum lunch duration (15 minutes), and work-section split threshold (10 minutes).
- Automatic lunch suggestion, manual pair selection, no-lunch selection, and undo.
- Optional daily meeting exception (off by default): a start between 09:25 and 09:40 and a maximum duration of 20 minutes. The selected meeting counts as work and keeps the work section continuous.
- Large, selectable dates/times; exact-second copy icons; individual section and whole-day copying as headerless Date/From/To TSV.
- A day's details hold three collapsible lists: Lock / unlock pairs (closed by default, with lunch and meeting selection), Work sections (open by default) and All log events. Opened or closed states are remembered while the app runs. Details append is disabled, with the reason shown, when no closed day follows the workbook's last date.
- The Work sections table shows date, unlock, lock, duration and an editable comment, with per-field copying and a leading date/time-row copy button.
- Section duration is displayed as `h:mm`, rounded to the nearest minute (30 seconds rounds up); copies and XLSX values retain their original precision. Compact leading columns leave the remaining width for the comment editor; each row stays a single line, with the save status beside the comment copy button.
- Section comments save locally as you type and are included in Details preview and XLSX writing. A section's date/start identity keeps its comment stable as an ongoing section closes.
- Ctrl+C / Ctrl+Insert and Ctrl+V / Shift+Insert. Display follows the workbook's date/time formats and Windows regional separators. Hidden seconds are not rounded; copy icons and Excel writing preserve original seconds.
- Hungarian and English UI; system, dark, and light themes.
- Native caption colors follow the selected theme, preview changes live and restore on Cancel. Windows 11 caption colors match the interface; older Windows versions keep their supported system rendering. High contrast uses system caption colors.
- A prominent disabled-logging warning opens the logging settings directly.
- Settings dialog, remembered font/window size, expanded days, and scroll position.
- Refresh re-reads the Security and System logs, the background monitor's data and the workbook's state, then recalculates every day. It also runs silently when the window is activated and once a minute while it stays active. An automatic refresh is skipped while you edit a field, have text selected, have Settings or an Excel preview open, or another operation is running, and at most one runs every 15 seconds.
- Settings use the available window height; only overflowing panel content scrolls, while Save and Cancel stay visible.
- A flat work-log-sheet-and-clock app icon is included in the executable, native window, interface and installer.
- Launching again from the Start menu or shortcut brings the existing window to the foreground and restores it when minimized.
- Optional background last-input tracking, disabled by default, with selectable lock-time or last-input endpoints for display, copying and Excel.
- `.xlsx` Details preview, append, optional completion of an unfinished row, backup, and detection of changes after preview.

## Install and run

Run the versioned `CompUsage-<version>-Setup-x64.exe`, or extract the entire portable ZIP and run `CompUsage.exe`. Keep the portable files together. The .NET runtime is included. The installer adds the Microsoft WebView2 Runtime if missing; that step requires an internet connection. On Windows 11 it is normally already present.

Setup registers a per-user Windows sign-in entry for the background collector and invokes it independently of the optional UI launch. Tracking defaults to disabled, so the collector exits without sampling until enabled in Settings → Time rules. Once enabled, it runs immediately, survives closing the window and starts again at subsequent sign-ins without opening the app. Disabling tracking stops it; the startup entry remains but exits immediately while disabled. Upgrade and uninstall stop the collector before replacing/removing its files; uninstall removes the startup entry and keeps saved data. The portable build can collect after enabling it in Settings, but Setup is needed to register automatic startup.

The application runs as the current user. Opening the app and refreshing never request UAC or start an elevated reader. If the Security log cannot be read, the app keeps showing saved data with a permission warning. An inaccessible audit-policy query is shown as unknown rather than disabled, and ordinary settings saves preserve that unknown state.

Settings → Time rules → Set up read access is an explicit, one-time Windows permission change. Approve its UAC prompt using the same Windows account. It grants that account read access to the **entire Security event log** (`EVT_READ_ACCESS`, 0x1) and system audit-policy queries (`AUDIT_QUERY_SYSTEM_POLICY`, 0x2). It does not grant log clearing, audit modification, administrator membership or a persistent security privilege. The app processes only that user's 4800/4801 events, interactive 4624 sign-ins, 4647 sign-outs and account-verified 4778/4779 session transitions. Existing ACL entries are preserved; explicit denies are not overridden. These account permissions persist across app upgrades/uninstall, and company policy can deny or revoke them. No installer or startup path applies this permission change automatically.

Changing Windows lock/unlock auditing can also request UAC when the current token lacks the required rights. The elevated helper is limited to explicitly requested Windows configuration operations; UI, activity collection, reading and workbook operations stay unelevated. After read access is configured, refresh always verifies it with the main process's normal token.

In Settings → Excel, select your `.xlsx` workbook. The `Details` worksheet must have Date, From, To, Duration, Duration (H), Details in columns A–F. Close the workbook in Excel before saving from CompUsage. An `.xls` file must first be saved as `.xlsx` in Excel.

## Time rules

The first observed unlock/connection and last observed lock/disconnection are shown for each workday. Lunch candidates must meet the minimum duration, last at most two hours, and begin within two hours of the expected lunch time; the closest start wins. A manual choice survives changes to those parameters.

The reader includes Security events 4778/4779 from the same auditing subcategory as 4800/4801. A reconnect opens a work interval and a disconnect closes it, including an RDP-to-console handover with no 4801 event. These records lack a SID and numeric session ID: the full domain/account must match the requested SID's Windows account before caching, and a numeric session is recovered only from an unambiguous matching SID/logon ID. A real lock/unlock within five seconds of a matching session transition takes precedence, preserving its exact time and existing lunch-pair identity. An intervening opposite transition prevents coalescing. Disconnecting an already locked session, for example when switching from a locked console to RDP, does not end work again. All original records remain in the expandable event list and its copy output. Repeated genuine lock events and ambiguous or multiple sessions still require review. No new audit-policy setting or background collector is needed for this support; missing historical audit events cannot be recreated.

Shutdown, restart and boot do not produce lock/unlock events, so the reader also includes the user's Security events 4647 (user-initiated sign-out, also written when shutting down or restarting from Start) and 4624 interactive, remote-interactive and cached-interactive sign-ins (logon types 2, 10 and 11). Windows also writes a short cached-interactive logon while checking credentials at each unlock; such a 4624 is ignored unless the same logon ID later appears in that user's lock, unlock, connection or sign-out events, or it is the logon session the app is running in, which covers today's sign-in before the first lock. A sign-in starts work and a sign-out ends it. A real lock/unlock or session transition within five seconds takes precedence, and, as with disconnects, a sign-out from an already locked or disconnected session does not end work again. A restart creates a new logon session: sessions that follow each other, each ending with a lock, disconnect or sign-out before the next begins, form one day, and the sign-out-to-sign-in gap is an ordinary break pair, so it splits sections only when longer than the threshold and can be selected as lunch or meeting. Interleaved sessions still require review. These events come from the Logon and Logoff audit subcategories, which Windows enables by default. 

Sleep, crashes and shutdowns without a sign-out are read from the System log, which ordinary users can read; if it is unavailable, the Security events still work. A Power-Troubleshooter 1 record, written on wake, gives the exact UTC start and end of a sleep or hibernation; overlapping periods, such as sleep followed by hibernation, merge into one. EventLog 6008, written at the next boot after a crash or power loss, supplies the system's last-alive time from its binary data, so that end is an estimate. Kernel-General 13 marks an orderly OS shutdown. These machine-wide records count only while the user's session is open, taking over that session's identity: a sleep after a lock, a crash while locked or a shutdown after a sign-out is ignored and hidden from the event list. Sleeping while signed in is an ordinary break pair, so it can be lunch or split sections. When the wake shows the lock screen, work restarts at the real unlock, and a lock recorded within 60 seconds of resuming repeats the sleep's end. After a crash, the next sign-in continues the day. Modern Standby without a lock is not interpreted.

Breaks strictly longer than the split threshold are excluded and split work rows. Shorter ordinary breaks count as work. The selected lunch break is always excluded, including a short manual lunch. Calculations use the original instants, not formatted display text. Workday grouping uses the local Windows time zone and handles daylight-saving boundaries.

Settings → Daily meeting contains **Ignore daily meeting**, its expected start window (09:25–09:40) and maximum length (20 minutes). This feature defaults to off. The window applies to the start, so a meeting can finish after 09:40. Both window endpoints and the maximum duration are inclusive, using original seconds. A window crossing midnight is supported. If several pairs qualify, the start closest to the window midpoint wins, with the earlier pair breaking a tie. Only one daily meeting is selected.

An enabled daily meeting exception takes precedence over the ordinary split threshold and the automatic lunch guess. An explicit lunch selection always takes precedence and remains excluded. The daily event list supports manual meeting selection, no-meeting selection, restoring the estimate, and undo. Like a manual lunch choice, a manual meeting overrides the estimate's time and length limits. Selecting an existing lunch pair as a meeting, or the reverse, clears the conflicting manual selection; undo restores both choices together. A stale selected pair requires review. Turning the feature off suspends its effect while retaining saved choices. The selected lock-time or last-input endpoint is used consistently for the meeting window, length and resulting work sections. Summary, TSV copying, Details preview and Excel writing all use the same calculation.

Current days and visibly incomplete or ambiguous event sequences are excluded from automatic append. A current day's finished sections can still be copied individually. Interleaved Windows sessions are marked for manual review. Windows cannot recover events from before auditing was enabled or after they were lost from the log; missing events cannot always be detected just from an alternating sequence. These are observed computer-unlock intervals, not proof of working time.

## Excel behavior

Preview proposes only complete days later than the last date already present. Today's ongoing day is appended too: a section still in progress is written with Date, From and comment only, leaving To and the durations blank. An unfinished row on the last date (Date and From filled, To empty) is offered separately; its existing start is preserved. It matches the recorded work section whose start is closest to the typed start within three minutes, including on today's ongoing day. Completion ends at the matching section's end, not across an excluded break; a section still open has nothing to complete yet. When the unfinished row is the sheet's last row, the day's later sections follow as new rows before any newer day, an open one again with a blank end, so the day continues up to the latest data. If the sheet's last row is complete, for example because it was appended during a break, the day's later sections are offered as an optional continuation after it. Otherwise further sections on an already-present date can be copied manually.

New rows contain numeric dates/times and durations. Their Details text comes from the section comment, or stays blank if no comment is entered. Comments are written as literal inline text, preserving Unicode, whitespace and Excel escape sequences; text beginning with `=` is not evaluated as a formula. When completing a selected unfinished row, an existing Details description or formula is preserved and shown in preview; only a blank Details cell is filled. Already exported rows are not rewritten.

Both the quick-copy buttons and the work-section table's leading copy button supply only the three Date/From/To columns (Excel A–C). A normal paste therefore preserves duration/hour formulas in D–E. Copy the comment separately with its own icon, or use Append to Details to write it into F. A single contiguous text paste cannot skip D–E while also filling F. The duration field's individual copy still includes original seconds. Open sections permit comment editing and individual field copying but have no final lock or full-row copy. Copying text for a manual paste follows Excel's normal paste interpretation.

Existing cell, row and column number formats are retained or inherited. The UI reads the Details date, From and To formats instead of forcing its own format. For example, `h:mm` displays `8:01:37` as `8:01`, while `hh:mm:ss` displays `08:01:37`. Regional separators and literal quoted separators are handled separately. Without an available workbook, the fallback is regional year/month/day and 24-hour time without a leading zero.

Other worksheets and unrelated workbook parts are preserved. Existing unfinished-row duration formulas are retained. The workbook is marked for recalculation by Excel. Both 1900 and 1904 date systems are supported. Plain worksheet ranges are the supported target; expanding structured Excel tables is not implemented.

Before replacement, the app checks the file's content hash against the preview and requires exclusive access. A backup named `<workbook>.backup-<timestamp>-<id>.xlsx` is created beside the original by Windows' atomic file-replacement operation. Some network shares deny that operation even with full control; the app then re-checks that the original still matches the preview, copies it to the backup and replaces it with a same-folder rename in one step. A workbook changed since the preview is never overwritten.

## Optional activity tracking

The unelevated `--activity-monitor` process samples the current Windows session's `GetLastInputInfo` about once per second while unlocked. On a Windows session-lock notification it saves the latest sample. It never captures keys, text, application names or screenshots, and never starts the elevated event-log helper. The main window shows collector health and automatic-start registration in Time rules; lock details show the last observed input and idle duration. A stopped collector can be restarted from the status row.

The time-source setting defaults to Windows lock time. Selecting last input changes lock endpoints consistently across the UI, lunch suggestion, break splitting, TSV copying and automatic Details append. Original Windows event timestamps and manual lunch-pair identities remain intact; the raw event list still shows the Windows times. Existing Excel rows are not rewritten when the mode changes. A missing, stale, ambiguous, different-session or invalid observation falls back to the original lock time. An input cannot move a lock before the preceding unlock or into a different workday. Both modes preserve source seconds.

This is an input-time estimate, not a guaranteed manual/automatic-lock classifier or proof of work. Win+L itself is input, and Windows can count software-generated input too. Samples from before tracking was enabled cannot be reconstructed. Collector history covers the 60-day UI range with a one-day retention margin for workday boundaries. No input is sampled while locked, disconnected or disabled.

## Local data

Preferences, manual lunch choices, cached events, and window state are stored in `%LocalAppData%\CompUsage\state.json`. The WebView2 profile lives in the same application data folder. Uninstall keeps this data and all workbooks. No event log, workbook, or user configuration belongs in the Git repository.

Manual daily meeting choices are stored alongside lunch choices in `state.json`, with the same 60-day retention. Existing settings files load with the feature disabled and the new defaults.

Section comments are stored in `SectionComments` in the same file, keyed by work date and exact UTC section start, with 60-day retention. Closing a section or adjusting its end does not change that key. Changing rules can merge/remove starts: comments for absent starts stay retained but are not silently assigned to another section; restoring the original section restores its comment. Emptying the editor removes the saved note. Comments are limited to Excel's 32,767 UTF-16 characters and valid XML text. Refresh/copy/export wait for pending saves, and close waits for the comment queue. A failed save retains its draft for retry; closing after a failure offers an explicit discard choice.

The optional monitor uses `%LocalAppData%\CompUsage\Activity\configuration.json`, separate session journals and small heartbeat files. The UI never writes the monitor's history, and the monitor never writes UI state. These files stay local and are retained on uninstall.

## Build

Prerequisites: Windows x64, .NET SDK 10.0.402, Node.js 22 or later, and PowerShell 7. Source comments and development documentation are in English.

```powershell
./build.ps1
```

The build restores packages, runs C# and UI checks, publishes a self-contained application, verifies the downloaded NSIS compiler by its pinned SHA-256 checksum, verifies Microsoft's signature on the WebView2 bootstrapper, and creates Setup, portable ZIP and `SHA256SUMS.txt` files under `artifacts/release/`. Application and package versions come from `<Version>` in `src/CompUsage.App/CompUsage.App.csproj`; direct NSIS invocation must supply `/DAPP_VERSION=x.y.z`. The bootstrapper URL follows Microsoft's Evergreen release; the compiler and SDK versions are pinned.

## GitHub builds and releases

Download installers and portable packages from [GitHub Releases](https://github.com/attixray/computer-usage/releases). Every push, pull request and manual workflow run builds and tests on a GitHub-hosted Windows runner. The resulting packages and checksums are also available as versioned artifacts on the [Actions page](https://github.com/attixray/computer-usage/actions).

To publish a new version, change only the application `<Version>`, commit it, and push a matching version tag such as `v1.0.6`. The workflow rejects a tag that does not match the project version. After all checks and packaging succeed, it verifies the package checksums and publishes a GitHub Release with Setup, the portable ZIP and the checksum file. Release publishing uses the workflow's repository-scoped token; no personal access token is stored in the project. Branch builds do not publish releases.

```powershell
git tag -a v1.0.6 -m "CompUsage 1.0.6"
git push origin main v1.0.6
```

## Development checks

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

`dotnet run --project tests/CompUsage.WindowsChecks` checks read-only ACL construction, read denial without elevation, explicit write escalation, a real non-elevating Security-log read, and native titlebar API acceptance on a hidden window. It never grants permissions, changes auditing or opens UAC. The actual one-time permission change remains an interactive Windows check.

`CompUsage.exe --verify-activity-monitor` checks disabled-by-default startup, an isolated collector process and heartbeat, duplicate prevention, a synthetic lock notification, graceful stop, and disabled subsequent startup. It uses a temporary journal, does not register startup, change auditing or lock the desktop, and cleans up after itself. The normal build runs it after publishing. A physical lock/unlock cycle and Windows sign-in remain manual end-to-end checks.

The build also runs `tests/installer-monitor-check.ps1`: it compiles the actual NSIS stop macro with a private scope and verifies graceful shutdown of an isolated background collector. It also round-trips the production message strings through a compiled Unicode installer to check Hungarian accents. It does not install the app or change startup registration. NSIS is invoked with `/INPUTCHARSET UTF8`; the main script also has a UTF-8 BOM for direct compiler invocation.

The icon's vector source is [design/app-icon.svg](design/app-icon.svg); rendering and palette notes are in [design/app-icon-prompt.md](design/app-icon-prompt.md). To rebuild the checked-in ICO and UI PNG from the rendered artwork on Windows, run `dotnet run --project tools/CompUsage.IconBuilder -- src/CompUsage.App/Assets/app-icon-source.png src/CompUsage.App/Assets/CompUsage.ico src/CompUsage.App/Ui/assets/app-icon.png`. The normal build consumes the checked-in assets and does not generate artwork.

## Structure

- `src/CompUsage.Core`: event parsing, time rules, work sections, workbook preview/write.
- `src/CompUsage.App`: WPF host, restricted native bridge, elevation helper, local state, and local UI.
- `tests/CompUsage.Checks`: deterministic time and workbook checks using generated fixtures.
- `tests/ui-checks.cjs`: DOM-based interaction and localization checks, without launching a browser.
- `installer`: NSIS installer and uninstall definition.

The release binaries are currently unsigned. Third-party components retain their own licenses; see `THIRD-PARTY-NOTICES.md`.
