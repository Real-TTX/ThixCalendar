# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What ThixCalendar is

A calendar for **Windows and macOS** (desktop app, tray / menu bar) that brings **Microsoft 365 / Outlook.com** (Graph),
**Google Calendar**, subscribed **ICS** addresses and calendars **on this computer** together – private and business in
one window. **The focus is the reminders** (owner's requirement: "Aktuell bekomme ich oft Termine nicht mit"): they stay
until answered, come again at the start (large, centred), play a sound (repeated), catch up after sleep, and join online
meetings with one click. UI language German; code, comments and identifiers English. The mockup of the main window is
`docs/mockups/hauptfenster.png`; the sister project ThixClip (`../ThixClip`) is the reference for structure and look.
**MatCloud** (the cloud of all Thix apps, `../MatCloud`, app id `calendar`, the general sync) keeps the accounts, the own
calendars with their events, the reminder settings and the answers to reminders the same on every computer; **ThixLauncher**
(`../ThixLauncher`, its catalog `AppCatalog`) installs ThixCalendar and hands over its MatCloud sign-in.

Stack: **.NET 10**, C#. **Avalonia 11.3** with **MatDesktop 0.2.0** (the desktop toolkit of MatDesign: Fluent in the colours of
the tokens, title bar, menu, tray, setup window), SQLite (Microsoft.Data.Sqlite), Ical.Net (ICS + recurrences), SkiaSharp
(tray badge). The desktop app follows MatDesign's rules DESK, TRAY and SETUP (MatDesign docs/DESKTOP.md).

## Build / run / test

- `dotnet build ThixCalendar.slnx` – expected: 0 warnings, 0 errors.
- `dotnet test tests/ThixCalendar.Tests` – reminder rules (pure `ReminderService.ComputeDue` with explicit times, and
  through the database), events / accounts / settings with validation and policies, Graph / Google / ICS parsing, sync
  against fake providers (`FakeHttp` replaces the network via `ProviderHttp.HandlerForTests`, `AccountService.SignInForTests`
  replaces the browser), the real OAuth loopback, event layout, time input, and the **UI rendered headless** (`UiTests`;
  with `THIXCALENDAR_SCREENSHOTS=<folder>` screenshots of every page and the reminder window in light and dark – look at
  them). `CloudTests`: sign-in (browser, password, ThixLauncher's hand-over) and two computers (two data folders) meeting in
  `FakeCloud` (MatCloud's general sync in memory). `CopyTests` hold the copies of MatDesktop and of the MatCloud contract.
  Tests never touch real data, Keychain, registry values or LaunchAgents (`TestData`), and never play a sound.
- Run with sample data: `THIXCALENDAR_DATA_DIR=<folder> THIXCALENDAR_SAMPLE_DATA=1 dotnet run --project src/ThixCalendar`
  (own instance, never meets the installed one; `SampleData` only fills an empty database in an override folder).
- The Windows publish (win-x64) is a **trimmed** single file (`TrimMode=partial`, Ical.Net rooted whole; about 40 MB).
  No `dynamic`, no built-in COM: COM goes through `[GeneratedComInterface]` (`Platform/Windows/ShellLink`).
  `ThixCalendar.exe --self-test <report>` checks the published program (JSON, SQLite, Ical.Net, provider JSON, token store,
  SkiaSharp, sound, COM, every window) in a temporary data folder; CI runs it after every Windows publish.
- `scripts/package-macos.sh` builds `ThixCalendar.app` (Info.plist, icns, ad-hoc signature), the update zip and the dmg.
- Icons: `cd tools && npm install && node render-icons.js` renders `branding/*.svg` (installed Edge, headless) into
  `branding/png` and `src/ThixCalendar/Assets`. The line icons of the app are MatDesktop's (every symbol of MatDesign's sprite,
  `<mat:MatIcon Icon="calendar"/>`). The few the sprite lacks (window, video, map-pin, bell-ring, bell-off, snooze,
  calendar-plus, today, rss) are drawn by `node tools/avalonia-icons.js` into `src/ThixCalendar/Themes/Icons.axaml` with keys
  `MatIcon.<name>`, so MatIcon finds them like its own (never edit that file by hand; better: add them to MatDesign's sprite).
- **MatDesktop** (`src/ThixCalendar/MatDesktop/`) is a COPY from MatDesign (branch `feature/desktop-and-services`,
  `src/MatDesktop`): never edit it here. Change it in MatDesign and copy again with
  `node tools/matdesktop-copy.js <path>/ThixCalendar/src/ThixCalendar` (there); `CopyTests` holds the files to `matdesktop.version`.
- **The MatCloud contract** (`src/ThixCalendar.Core/MatCloud/`, namespace `MatCloud.Contract`) is a COPY: change it in MatCloud
  and copy again with `node ../MatCloud/tools/contract-copy.js src/ThixCalendar.Core`. Run MatCloud locally with
  `../MatCloud/scripts/redeploy.ps1` (http://localhost:4950).

## Map

```
src/ThixCalendar.Core   (no UI) models, business layer (static classes), infrastructure, providers, native bits
  Models/               CalendarAccount (Kind Local/Microsoft/Google/Ics, State), CalendarInfo, CalendarEvent (absolute
                        Start/End; all-day = dates at midnight UTC, StartLocal/EndLocal; OccurrenceKey), EventItem,
                        EventQuery, CalendarColors, AppSettings (reminders, sync, app registrations, updates, CloudSettings),
                        BusinessException; an account has a SyncId, an own calendar and its events a key of their own (RemoteId)
  Business/             CalendarService (queries, save/delete – provider first, then the store; validation and permission:
                        read-only calendars, signed-out accounts, policies), AccountService (local / ICS / Microsoft / Google,
                        sign-in, access tokens in memory with refresh + rotation, MarkSignedOut), SyncService (loop every N
                        minutes + Kick, window −14 … +120 days, calendar reconcile, EnsureRangeAsync per month on demand),
                        ReminderService (ComputeDue: stages Before / Start / AllDay, answers per occurrence, missed window,
                        snooze, pause; Check/Dismiss/Snooze/Upcoming), SettingsService, AccessPolicy (LockConfiguration,
                        DisableOnlineAccounts, DisableUpdates), AutostartService (HKCU Run / LaunchAgent), InstallService
                        (Windows self-installing exe), UpdateService + MacAppBundle (OTA from GitHub releases), SampleData,
                        CloudService (MatCloud: sign-in in the browser / with a password / ThixLauncher's hand-over, the loop
                        pull -> push -> long poll, 401 signs out, 403 waits), CloudItems (what travels: settings, accounts
                        WITHOUT sign-ins, calendars – for online ones only what the user chose –, the events of the own
                        calendars, the answers to reminders; index cloud-sync.json; items that cannot be applied yet are parked)
  Infrastructure/       CalendarStore (SQLite calendar.db: Account, Calendar, CalendarEvent, ReminderState; ReplaceEvents keeps
                        row ids of known remote keys), SettingsStore (settings.json), TokenStore (DPAPI / Keychain), AppPaths,
                        AppInfo (version, channel, built-in client ids), Log, ProblemReports (crash / hang reports)
  Providers/            ICalendarProvider; GraphProvider (calendarView expanded, Prefer: local time zone + text body, PATCH
                        only changed fields), GoogleProvider (singleEvents, popup reminders, attendee answer), IcsProvider
                        (Ical.Net, read-only), OAuthClient (PKCE, loopback TcpListener on IPv4+IPv6, token refresh),
                        ProviderHttp (one HttpClient, JsonNode only, ProviderException), MeetingLinks (Teams/Meet/Zoom/Webex)
  MatCloud/             COPY of MatCloud's contract (never edited here): CloudContract, CloudApps, Devices, Handoff (CloudHandoff),
                        Sync (the general sync), MatCloudClient
  Platform/             Chime (own two-note WAV via winmm on Windows, afplay "Glass" on macOS), WindowActivation, Windows/ShellLink
src/ThixCalendar        (Avalonia app)
  Program / App         start-up: ProblemReports first, self test, Windows setup mode, single instance (mutex + pipe), tray,
                        sync, MatCloud (+ ThixLauncher's sign-in), reminders; Initialize installs MatDesktop's theme with
                        ThixCalendar's accent (#2F6FED), then Themes/Icons.axaml and Themes/Theme.axaml; ApplyTheme =
                        MatTheme.SetAppearance; ToggleReminderPauseAsync, ExitApplication
  MatDesktop/           COPY of MatDesign's desktop toolkit (never edited here): MatTheme, MatWindow, MatTitleBar, MatNav,
                        MatIcon, MatSettingRow/MatFormField, MatTray/MatTrayMenu, MatNotification, MatSetupWindow, MatVersion …
  MainWindow            a MatWindow like ThixClip: MatTitleBar (logo and word mark, search over all events → "Termine",
                        "Neuer Termin", caption buttons on Windows / traffic lights on macOS), MatNav (Kalender, Termine,
                        Konten, Einstellungen; at its foot "Meine Kalender" with coloured switches, MiniMonth – only where it
                        does not push the calendars out (FitMenuFoot) –, the state of the reminders and of MatCloud; version =
                        about, fold with Ctrl/Cmd+B), the page beside it; a back stack for the editors; keyboard (OnPreviewKeyDown)
  Pages/                CalendarPage (the grid gets the room – owner's requirement: ONE slim toolbar with ‹ Heute ›,
                        period + KW, segments Tag | Arbeitswoche | Woche | Monat, sync, filter with accounts and
                        calendars; no big title, no chips row), EventsPage (CRUD list: the header search,
                        toolbar calendar / period / sort, list, Pager, actions), EventEditorPage (tabs Termin | Notizen | Erinnerung),
                        AccountsPage (CRUD list), AccountEditorPage (tabs Allgemein | Kalender), SettingsPage (tabs Allgemein |
                        Erinnerungen | Synchronisierung | Cloud | Updates | Erweitert – own app registrations folded there for
                        admins; Cloud in SettingsPage.Cloud.cs: like ThixClip, in the browser first, user name and password
                        second), AboutPage
  Controls/             ONE code base per control: TimeGridView (+ DayColumnPanel, EventLayout lanes), MonthView, MiniMonth,
                        EventVisuals (block, chip, tool tip), Pager, TimeField, ColorSwatches, AccountMark, ActionBar,
                        FormErrors (controls:FormErrors.Field on a MatFormField = BusinessException.Field), InfoBar, IconLabel
                        (a sprite icon by name and a text)
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
  business layer. Errors are `BusinessException(message, field)`; the UI shows them at the `MatFormField` whose
  `controls:FormErrors.Field` is that `Field`.
  Provider failures (`ProviderException`) are turned into `BusinessException` at the service boundary.
- **Relational rules** (SQLite): PascalCase tables/columns, key `Id` (64 bit), `CreateDate`, `CreateUserId`, `UpdateDate`,
  `UpdateUserId`, `UpdateState` (1 created, 2 updated). Rows are deleted for real (the providers are the truth).
- **MatCloud rules** (CloudItems): the general sync, last writer wins by `UpdatedAt`, a delete leaves a tombstone. Sign-ins at
  Microsoft and Google NEVER travel (an account from another computer arrives as "Anmeldung nötig"); the calendars of
  Microsoft, Google and ICS stay theirs (only shown / reminders / an own colour travel); an answer to a reminder travels by its
  occurrence as every computer names it (calendar key : event key : start). What cannot be applied yet is parked, never deleted.
- **Providers are the truth**: a change goes to Microsoft / Google first; only what they accepted is stored. Graph gets only
  the changed fields (formatted notes are never overwritten unless the notes changed). Never log event contents, passwords
  or tokens.
- **Reminders must not be missed**: never auto-close a reminder, never steal the keyboard (`ShowActivated = false`), keep
  answers per occurrence (`OccurrenceKey`), keep the rules in `ReminderService.ComputeDue` (pure, tested).
- **UI rules:** toolbar above lists (search, filter, sort), paging below the list, actions below, left-aligned, positive to
  negative, delete with a gap; buttons in one row (Speichern, …, Zurück … gap … Löschen); icons where they help; tabs for
  areas of one item; hide what does not apply (times for all-day events, URL only for ICS); cards keep their distance;
  the about page opens from the version bottom left. **Colours only from MatDesktop's theme** (DESK-02):
  `{DynamicResource Mat.Surface}`, `Mat.TextMuted`, `Mat.Accent` … in XAML, `control.Use(property, "Mat.TextMuted")` (or
  `Ui.Brush`) in code; named looks are MatDesktop's control themes (`MatButton.Primary`, `.Danger`, `.Link`, `MatText.PageTitle`,
  `MatCard`, `MatSearchBox` …); ThixCalendar's own few (Segment, RoundButton, PlainRow, SubtleCard) in `Themes/Theme.axaml`
  use only Mat.* tokens. The calendar colours are data: `Ui.SolidBrush` / `Ui.Tint`. Icon-only buttons get `AutomationProperties.Name`.
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
  The **Microsoft client id is built in** (`Directory.Build.props`, ThixCalendar's Entra registration, multi-tenant + personal;
  a public client id is no secret). Optional secrets `THIXCALENDAR_GOOGLE_CLIENT_ID`, `THIXCALENDAR_GOOGLE_CLIENT_SECRET`
  (and `THIXCALENDAR_MICROSOFT_CLIENT_ID` to override) become the built-in app registrations (MSBuild reads them from the
  environment); local builds read secrets.local.props (ignored by git, template secrets.local.props.example). **Users never enter client ids: they just sign in**
  (owner's requirement) – the registration is the publisher's one-time job (docs/APP-REGISTRIERUNG.md).
