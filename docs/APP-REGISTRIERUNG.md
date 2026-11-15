# App-Registrierung (einmalig, durch den Herausgeber)

**Nutzer von ThixCalendar müssen hiervon nichts wissen:** Sie klicken Konten › Konto hinzufügen › *Microsoft 365* bzw.
*Google Kalender*, melden sich im Browser mit ihrem Entra- / Microsoft- bzw. Google-Konto an und erlauben den Zugriff.

Damit das geht, verlangen Microsoft und Google, dass die **App selbst** einmal bei ihnen bekannt ist (so wie Outlook,
Thunderbird oder Apple Kalender auch). Diese Kennung baut man in ThixCalendar ein – danach gilt sie für alle Nutzer
und alle Konten (Arbeit, privat, andere Firmen).

## 1. Microsoft (Entra ID + private Microsoft-Konten) – ca. 3 Minuten

1. <https://entra.microsoft.com> › **Identität › Anwendungen › App-Registrierungen › Neue Registrierung**.
2. Name: `ThixCalendar`.
   Unterstützte Kontotypen: **Konten in einem beliebigen Organisationsverzeichnis und persönliche Microsoft-Konten**.
   Umleitungs-URI: Plattform **Öffentlicher Client/nativ (mobil und Desktop)**, Wert `http://localhost`. › *Registrieren*.
3. **API-Berechtigungen** › Berechtigung hinzufügen › Microsoft Graph › *Delegierte Berechtigungen*:
   `Calendars.ReadWrite`, `User.Read`, `offline_access` (und `openid`, `profile`).
   Optional: *Administratorzustimmung für <Organisation> erteilen* – dann fragt in der eigenen Firma niemand mehr.
4. **Authentifizierung** › Erweiterte Einstellungen › *Öffentliche Clientflows zulassen*: **Ja** › Speichern.
5. **Übersicht** › *Anwendungs-ID (Client-ID)* kopieren. Ein Geheimnis wird **nicht** gebraucht.

Andere Firmen: Erlaubt deren IT Nutzern die Zustimmung zu Apps, klappt die Anmeldung sofort; sonst erteilt deren
Administrator einmal die Zustimmung (der Anmeldedialog bietet das an).

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

- **GitHub (alle CI-Builds):** Repository › Settings › Secrets and variables › Actions:
  `THIXCALENDAR_MICROSOFT_CLIENT_ID`, `THIXCALENDAR_GOOGLE_CLIENT_ID`, `THIXCALENDAR_GOOGLE_CLIENT_SECRET`.
- **Lokale Builds:** `secrets.local.props.example` nach `secrets.local.props` kopieren und ausfüllen (von git ignoriert).

Danach zeigt Einstellungen › Erweitert › *Eigene App-Registrierung* „eingebaut“ – und niemand muss dort etwas eintragen.
Das Feld bleibt nur für Firmen, die fremde Apps sperren und eine eigene Registrierung verwenden wollen.
