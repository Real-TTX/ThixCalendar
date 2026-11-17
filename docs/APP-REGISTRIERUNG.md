# App-Registrierung (einmalig, durch den Herausgeber)

**Nutzer von ThixCalendar müssen hiervon nichts wissen:** Sie klicken Konten › Konto hinzufügen › *Microsoft 365* bzw.
*Google Kalender*, melden sich im Browser mit ihrem Entra- / Microsoft- bzw. Google-Konto an und erlauben den Zugriff.

Damit das geht, verlangen Microsoft und Google, dass die **App selbst** einmal bei ihnen bekannt ist (so wie Outlook,
Thunderbird oder Apple Kalender auch). Diese Kennung baut man in ThixCalendar ein – danach gilt sie für alle Nutzer
und alle Konten (Arbeit, privat, andere Firmen).

## 1. Microsoft (Entra ID + private Microsoft-Konten) – ca. 3 Minuten

**Mit welchem Konto?** Mit dem **Geschäftskonto** der eigenen Firma (Entra-Mandant), das App-Registrierungen anlegen
darf – nicht mit einem privaten Microsoft-Konto. Ein privates Konto hat kein eigenes Verzeichnis; das Portal landet dann
im Mandanten „Microsoft Services“ und meldet *„Das ausgewählte Benutzerkonto ist im Mandanten "Microsoft Services" nicht
vorhanden und kann auf die Anwendung "74658136-…" nicht zugreifen“* (74658136-… ist das Azure-/Entra-Portal selbst, nicht
ThixCalendar). Abhilfe: im Anmeldefenster *Anderes Konto verwenden* und das Firmenkonto wählen, oder direkt
`https://entra.microsoft.com/<firmendomain>` öffnen. Die Registrierung gehört dann der Firma, gilt aber trotzdem für alle
(Arbeit, andere Firmen, private Outlook.com-Konten).

1. <https://entra.microsoft.com> › **Identität › Anwendungen › App-Registrierungen › Neue Registrierung**.
   (Wer über *Unternehmensanwendungen › Neue Anwendung › Eigene Anwendung erstellen* kommt, wählt dort *Anwendung für
   die Integration in Microsoft Entra ID registrieren (von Ihnen entwickelte App)* – nicht Anwendungsproxy und nicht
   „nicht im Katalog gefunden“.)
2. Name: `ThixCalendar`.
   Unterstützte Kontotypen: **Konten in einem beliebigen Organisationsverzeichnis und persönliche Microsoft-Konten**
   (nur so können sich alle Nutzer mit ihrem eigenen Konto anmelden – sie müssen nicht im eigenen Mandanten sein).
   Umleitungs-URI: Plattform **Öffentlicher Client/nativ (mobil und Desktop)**, Wert `http://localhost`. › *Registrieren*.
3. **API-Berechtigungen** › Berechtigung hinzufügen › Microsoft Graph › *Delegierte Berechtigungen*:
   `Calendars.ReadWrite`, `User.Read`, `offline_access` (und `openid`, `profile`).
   Optional: *Administratorzustimmung für <Organisation> erteilen* – dann fragt in der eigenen Firma niemand mehr.
4. **Authentifizierung** › Erweiterte Einstellungen › *Öffentliche Clientflows zulassen*: **Ja** › Speichern.
5. **Übersicht** › *Anwendungs-ID (Client-ID)* kopieren. Ein Geheimnis wird **nicht** gebraucht.

**Wer sich anmelden kann:**

| Konto | Was passiert |
|---|---|
| Eigene Firma | Sofort; nach der Administratorzustimmung (Schritt 3) ohne Rückfrage. |
| Privat (Outlook.com, Hotmail, Live) | Sofort; einmal „Zugriff erlauben“ bestätigen. |
| Andere Firmen | Erlaubt deren IT Nutzern die Zustimmung zu Apps, sofort. Viele Firmen erlauben das aber nur für Apps von **verifizierten Herausgebern** – dann zeigt die Anmeldung „Administratorgenehmigung erforderlich“, und deren Administrator stimmt einmal zu (der Anmeldedialog bietet das an). |

*Unternehmensanwendungen › ThixCalendar › Eigenschaften › Zuweisung erforderlich* bleibt auf **Nein** (Standard) – sonst
kommen nur ausdrücklich zugewiesene Personen hinein.

**Später (optional): Herausgeberüberprüfung.** Damit der Anmeldedialog „verifiziert“ zeigt und Nutzer anderer Firmen die
App meist selbst zulassen dürfen: kostenlose Partner-ID (MPN-ID) im *Microsoft AI Cloud Partner Program* (Partner Center,
mit dem Firmenkonto; Microsoft prüft die Firma, das dauert einige Tage), dann App-Registrierung › *Branding und
Eigenschaften* › Herausgeberdomäne `matthix.de` bestätigen und die MPN-ID eintragen. ThixCalendar selbst ändert sich dabei
nicht – die Client-ID bleibt dieselbe.

## 2. Google – ca. 5 Minuten

1. <https://console.cloud.google.com> › neues Projekt `ThixCalendar`.
2. **APIs & Dienste › Bibliothek** › *Google Calendar API* › Aktivieren.
3. **Google Auth Platform / OAuth-Zustimmungsbildschirm**: Typ *Extern*, App-Name `ThixCalendar`, Support-E-Mail.
   Bereich (Datenzugriff) hinzufügen: `https://www.googleapis.com/auth/calendar`.
4. **Zielgruppe / Veröffentlichungsstatus: „In Produktion“ setzen.** Wichtig: Im Status *Test* laufen Anmeldungen nach
   7 Tagen ab. Ohne Google-Prüfung zeigt die Anmeldung einmal „Google hat diese App nicht überprüft“ › *Erweitert* ›
   *Weiter zu ThixCalendar* (bis 100 Nutzer ohne Prüfung; für mehr reicht man die App bei Google zur Prüfung ein).
5. **Clients / Anmeldedaten › OAuth-Client-ID erstellen** › Anwendungstyp **Desktop-App** › Client-ID und Client-Secret
   kopieren (bei Desktop-Apps laut Google kein echtes Geheimnis).

## 3. Einbauen

- **Microsoft:** Die Client-ID steht direkt in `Directory.Build.props` (`MicrosoftClientId`) – sie ist kein Geheimnis
  (sie steht in jeder Anmeldeadresse). Jeder Build, lokal und in CI, hat sie damit eingebaut.
  Eingebaut: `67da608f-1ccd-4664-94d7-fd12ea31f063` (Entra-Registrierung „ThixCalendar“).
- **Google – Client-ID:** steht ebenfalls in `Directory.Build.props` (`GoogleClientId`), eingebaut:
  `466427641870-qud3jgehsu1qsgnb92rq27qve5cbifs6.apps.googleusercontent.com`.
- **Google – Client-Secret (alle CI-Builds):** Repository › Settings › Secrets and variables › Actions › *New repository
  secret* `THIXCALENDAR_GOOGLE_CLIENT_SECRET` (`GOCSPX-…`). Es steht bewusst **nicht** im Repository: GitHubs Push-Schutz
  sperrt Google-Secrets. Ohne Secret zeigt ThixCalendar Google als „noch nicht eingerichtet“ (Google tauscht die Anmeldung
  nur mit Secret ein).
- **Google – lokale Builds:** `secrets.local.props.example` nach `secrets.local.props` kopieren und das Secret eintragen
  (von git ignoriert).
- Überschreiben (z. B. Test-Registrierung): `THIXCALENDAR_MICROSOFT_CLIENT_ID` als Secret bzw. `MicrosoftClientId` in
  `secrets.local.props`.

Danach zeigt Einstellungen › Erweitert › *Eigene App-Registrierung* „eingebaut“ – und niemand muss dort etwas eintragen.
Das Feld bleibt nur für Firmen, die fremde Apps sperren und eine eigene Registrierung verwenden wollen.
