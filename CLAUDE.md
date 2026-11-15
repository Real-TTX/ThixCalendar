# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What ThixCalendar is

A calendar for **Windows and macOS** (desktop app, tray / menu bar) that brings **Microsoft 365 / Outlook.com** (Graph),
**Google Calendar**, subscribed **ICS** addresses and calendars **on this computer** together – private and business in
one window. **The focus is the reminders** (owner's requirement: "Aktuell bekomme ich oft Termine nicht mit"): they stay
until answered, come again at the start (large, centred), play a sound (repeated), catch up after sleep, and join online
meetings with one click. UI language German; code, comments and identifiers English. The mockup of the main window is
`docs/mockups/hauptfenster.png`; the sister project ThixClip (`../ThixClip`) is the reference for structure and look.

Stack: **.NET 10**, C#. **Avalonia 11.3** (Fluent underneath our own look), SQLite (Microsoft.Data.Sqlite), Ical.Net
(ICS + recurrences), SkiaSharp (tray badge).

## Build / run / test

- `dotnet build ThixCalendar.slnx` – expected: 0 warnings, 0 errors.
- `dotnet test tests/ThixCalendar.Tests` – reminder rules (pure `ReminderService.ComputeDue` with explicit times, and
  through the database), events / accounts / settings with validation and policies, Graph / Google / ICS parsing, sync
  against fake providers (`FakeHttp` replaces the network via `ProviderHttp.HandlerForTests`, `AccountService.SignInForTests`
  replaces the browser), the real OAuth loopback, event layout, time input, and the **UI rendered headless** (`UiTests`;
  with `THIXCALENDAR_SCREENSHOTS=<folder>` screenshots of every page and the reminder window in light and dark – look at
  them). Tests never touch real data, Keychain, registry values or LaunchAgents (`TestData`), and never play a sound.
- Run with sample data: `THIXCALENDAR_DATA_DIR=<folder> THIXCALENDAR_SAMPLE_DATA=1 dotnet run --project src/ThixCalendar`
  (own instance, never meets the installed one; `SampleData` only fills an empty database in an override folder).
- The Windows publish (win-x64) is a **trimmed** single file (`TrimMode=partial`, Ical.Net rooted whole; about 40 MB).
  No `dynamic`, no built-in COM: COM goes through `[GeneratedComInterface]` (`Platform/Windows/ShellLink`).
  `ThixCalendar.exe --self-test <report>` checks the published program (JSON, SQLite, Ical.Net, provider JSON, token store,
  SkiaSharp, sound, COM, every window) in a temporary data folder; CI runs it after every Windows publish.
- `scripts/package-macos.sh` builds `ThixCalendar.app` (Info.plist, icns, ad-hoc signature), the update zip and the dmg.
- Icons: `cd tools && npm install && node render-icons.js` renders `branding/*.svg` (installed Edge, headless) into
  `branding/png` and `src/ThixCalendar/Assets`. `node tools/avalonia-icons.js` turns MatDesign's icon sprite
  (`../MatDesign`) plus a few own ones into `src/ThixCalendar/Themes/Icons.axaml` (never edit that file by hand).

## Map

```
src/ThixCalendar.Core   (no UI) models, business layer (static classes), infrastructure, providers, native bits
  Models/               CalendarAccount (Kind Local/Microsoft/Google/Ics, State), CalendarInfo, CalendarEvent (absolute
                        Start/End; all-day = dates at midnight UTC, StartLocal/EndLocal; OccurrenceKey), EventItem,
                        EventQuery, CalendarColors, AppSettings (reminders, sync, app registrations, updates), BusinessException
  Business/             CalendarService (queries, save/delete – provider first, then the store; validation and permission:
                        read-only calendars, signed-out accounts, policies), AccountService (local / ICS / Microsoft / Google,
                        sign-in, access tokens in memory with refresh + rotation, MarkSignedOut), SyncService (loop every N
                        minutes + Kick, window −14 … +120 days, calendar reconcile, EnsureRangeAsync per month on demand),
                        ReminderService (ComputeDue: stages Before / Start / AllDay, answers per occurrence, missed window,
                        snooze, pause; Check/Dismiss/Snooze/Upcoming), SettingsService, AccessPolicy (LockConfiguration,
                        DisableOnlineAccounts, DisableUpdates), AutostartService (HKCU Run / LaunchAgent), InstallService
                        (Windows self-installing exe), UpdateService + MacAppBundle (OTA from GitHub releases), SampleData
  Infrastructure/       CalendarStore (SQLite calendar.db: Account, Calendar, CalendarEvent, ReminderState; ReplaceEvents keeps
                        row ids of known remote keys), SettingsStore (settings.json), TokenStore (DPAPI / Keychain), AppPaths,
                        AppInfo (version, channel, built-in client ids), Log, ProblemReports (crash / hang reports)
  Providers/            ICalendarProvider; GraphProvider (calendarView expanded, Prefer: local time zone + text body, PATCH
                        only changed fields), GoogleProvider (singleEvents, popup reminders, attendee answer), IcsProvider
                        (Ical.Net, read-only), OAuthClient (PKCE, loopback TcpListener on IPv4+IPv6, token refresh),
                        ProviderHttp (one HttpClient, JsonNode only, ProviderException), MeetingLinks (Teams/Meet/Zoom/Webex)
  Platform/             Chime (own two-note WAV via winmm on Windows, afplay "Glass" on macOS), WindowActivation, Windows/ShellLink
src/ThixCalendar        (Avalonia app)
  Program / App         start-up: ProblemReports first, self test, Windows setup mode, single instance (mutex + pipe), tray,
                        sync, reminders; ShowMainWindow, ToggleReminderPauseAsync, ExitApplication
  MainWindow            title bar (own caption buttons on Windows / traffic lights on macOS), sidebar card (sections, "Meine
                        Kalender" with coloured switches, MiniMonth, reminder state, version = about, fold with Ctrl/Cmd+B),
                        page card; navigation with a back stack for the editors; keyboard (OnPreviewKeyDown)
  Pages/                CalendarPage (toolbar: title, search, sync, filter, "Neuer Termin"; account chips; ‹ Heute ›, period
                        title + KW, segments Tag | Arbeitswoche | Woche | Monat), EventsPage (CRUD list: toolbar search /
                        calendar / period / sort, list, Pager, actions), EventEditorPage (tabs Termin | Notizen | Erinnerung),
                        AccountsPage (CRUD list), AccountEditorPage (tabs Allgemein | Kalender), SettingsPage (tabs Allgemein |
                        Erinnerungen | Synchronisierung | Konten-Zugang | Updates | Erweitert), AboutPage
  Controls/             ONE code base per control: TimeGridView (+ DayColumnPanel, EventLayout lanes), MonthView, MiniMonth,
                        EventVisuals (block, chip, tool tip), Pager, TimeField, ColorSwatches, AccountMark, ActionBar,
                        FormField, SettingRow, InfoBar, IconLabel, IconView
  Reminders/            ReminderCoordinator (10 s check off the UI thread, sound + repeat, test reminder, answers),
                        ReminderWindow (topmost, never activated, corner; centred with "Beginnt jetzt" at the start)
  Tray/                 TrayIcon (badge: red = reminders waiting, amber = paused; next events; pause; sync; quit), TrayIconImages,
                        NotificationToast
  Setup/                SetupWindow (Windows install / update / uninstall assistant)
  Dialogs/              ConfirmDialog, SignInDialog (waits for the browser sign-in), PauseDialog, ProblemReportDialog
tests/ThixCalendar.Tests
```

## Conventions

- **Microsoft C# coding conventions**, file-scoped namespaces, `_camelCase` fields, human-readable names, early returns.
- **Business layer = static classes.** UI never touches `CalendarStore`, `SettingsStore`, files, registry or HTTP; pages
  never query. Validation **and permission checks** (AccessPolicy, read-only calendars, account state) live in the
  business layer. Errors are `BusinessException(message, field)`; the UI shows them at the `FormField` with that `Field`.
  Provider failures (`ProviderException`) are turned into `BusinessException` at the service boundary.
- **Relational rules** (SQLite): PascalCase tables/columns, key `Id` (64 bit), `CreateDate`, `CreateUserId`, `UpdateDate`,
  `UpdateUserId`, `UpdateState` (1 created, 2 updated). Rows are deleted for real (the providers are the truth).
- **Providers are the truth**: a change goes to Microsoft / Google first; only what they accepted is stored. Graph gets only
  the changed fields (formatted notes are never overwritten unless the notes changed). Never log event contents, passwords
  or tokens.
- **Reminders must not be missed**: never auto-close a reminder, never steal the keyboard (`ShowActivated = false`), keep
  answers per occurrence (`OccurrenceKey`), keep the rules in `ReminderService.ComputeDue` (pure, tested).
- **UI rules:** toolbar above lists (search, filter, sort), paging below the list, actions below, left-aligned, positive to
  negative, delete with a gap; buttons in one row (Speichern, …, Zurück … gap … Löschen); icons where they help; tabs for
  areas of one item; hide what does not apply (times for all-day events, URL only for ICS); cards keep their distance;
  the about page opens from the version bottom left. Colours only from `Themes/Theme.axaml` via `DynamicResource` (light
  and dark), calendar colours via `Ui.SolidBrush` / `Ui.Tint`. Icon-only buttons get `AutomationProperties.Name`.
  Several instances of a control on one page must work (no static state in controls).
- **Avalonia:** ControlThemes cannot contain descendant selectors – put those styles on the page. `Panel.Render` is
  sealed (draw in a child, see `DayColumnPanel.HourLines`). Rows built in code must not set colours locally that a page
  style should change (`Border.row`).
- **Platforms:** one code base. Differences behind `OperatingSystem.IsWindows()/IsMacOS()`. macOS cannot be checked from
  the Windows machine: CI builds and tests it, the owner tries it on a Mac (menu bar, Keychain, sound, LaunchAgent).
- Edit text files with the editor tools or Node/`[IO.File]` with UTF-8: Windows PowerShell 5.1 `Get-Content`/`Set-Content`
  double-encode umlauts.

## Versioning, branches, CI

- `Directory.Build.props` holds major/minor. CI sets `Channel`, `BuildNumber` (GitHub run number of the workflow),
  `BuildDate`: release `0.1.42-20261115` (main), nightly `nightly-43-20261115` (dev), local `local-20261115` (everything else).
- Branches: `main` = release, `dev` = nightly. **Commits are authored by Matthias Schmoldt only – no co-author trailers, no
  other names, no "generated with" lines.**
- `.github/workflows/desktop.yml` (every branch): Windows (test, publish win-x64 single file = `ThixCalendarSetup.exe`, self
  test), macOS (test, publish osx-arm64 + osx-x64, `package-macos.sh` → dmg/zip), publish (manifest
  `thixcalendar-update.json`, rolling releases `release-channel` / `nightly-channel`, versioned release `v<version>` on main).
  Optional secrets `THIXCALENDAR_MICROSOFT_CLIENT_ID`, `THIXCALENDAR_GOOGLE_CLIENT_ID`, `THIXCALENDAR_GOOGLE_CLIENT_SECRET`
  become the built-in app registrations (MSBuild reads them from the environment).
