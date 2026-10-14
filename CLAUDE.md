# CLAUDE.md – m2v / d2c DataUpload MQTT

Windows-Tool (WPF, .NET 10) von SPÄNI Elektro-Mechanik AG: sendet CSV-Zeilen gedrosselt als MQTT-Nachrichten
(Nachlieferung in die Cloud). Öffentliches GitHub-Repository, MIT-Lizenz, Verteilung als portable ZIP über
GitHub Releases, Update-Hinweis im Programm. Sprache im Projekt: Deutsch (Code-Kommentare, Commits, Doku);
Oberfläche DE/EN.

## Befehle

```bash
dotnet build D2cDataUpload.slnx -c Release                 # alles (WPF-Teil nur unter Windows ausführbar)
dotnet run --project src/D2cDataUpload.App                  # Programm starten (Windows)
dotnet run --project tests/D2cDataUpload.Tests              # Tests; Broker-Tests nur mit "mosquitto" im PATH
D2C_TLS_CONF="<mosquitto.conf>;<port>;<ca.crt>;<user>;<pw>" dotnet run --project tests/D2cDataUpload.Tests   # + TLS-Test
```
Es gibt kein Test-Framework: `tests/D2cDataUpload.Tests/Program.cs` ist ein Konsolen-Testlauf (`Test("Name", () => …)`),
Exit-Code ≠ 0 bei Fehlern. Neue Tests dort im passenden Abschnitt ergänzen.

## Aufbau

- `Directory.Build.props` – **einzige Stelle** für `<Version>` und `<GitHubRepository>` (wird als AssemblyMetadata
  ins Programm eingebaut und von der Update-Prüfung gelesen), Firma, Lizenz.
- `src/D2cDataUpload.Core` (net10.0, **keine NuGet-Pakete**, ohne UI, auf Linux testbar)
  - `CsvSource` – streamendes RFC-4180-Lesen, Trennzeichen-Erkennung, Encodings
  - `PayloadBuilder` – Zeile → (Topic, Payload); Modi Spalte/Vorlage bzw. Spalte/JSON; `RowException` = Zeile überspringen, `MappingException` = Konfiguration falsch
  - `SendJob` – Drosselung, Ack-Fenster, Reconnect mit erneutem Senden, Fortsetzungspunkt (Checkpoint)
  - `Mqtt/MiniMqttClient` – eigener MQTT-3.1.1-Client (QoS 0/1/2, TLS, Auth, Keepalive)
  - `AppSettings` – JSON in `%APPDATA%\d2c_DataUpload_MQTT\settings.json`; `SecretStore` – DPAPI für das Passwort
  - `Loc` + `Strings` – Übersetzung DE/EN; `HelpServer` – lokaler Webserver für `Help/`; `UpdateChecker` – GitHub Releases
- `src/D2cDataUpload.App` (net10.0-windows, WPF, Code-Behind, kein MVVM-Framework)
  - `App.xaml` – alle Farben/Styles (`AccentBrush`, `PrimaryButton`, `HelpButton`, GroupBox als Karte, `MonoBox` …)
  - `MainWindow` – Menü, Update-Banner, Tabs, Footer; `AboutWindow` – Hilfe › Info
  - `Views/SendView` (Datei, Vorschau, Zuordnung, Senden), `Views/SettingsView`
  - `Infrastructure/AppInfo.cs` – Titel, Firma, Entwickler, Support; `TrExtension` – `{l:Tr Key}`
  - `Help/de.html`, `Help/en.html`, `style.css` – Hilfe (gleiche `id`-Anker in beiden Sprachen)
- `.github/workflows/ci.yml` (Linux-Tests mit Mosquitto + Windows-Build), `release.yml` (Tag `v*.*.*` → ZIP + Release)

## Regeln (bitte einhalten)

1. **Texte**: Jeder sichtbare Text über `Loc` – Schlüssel in `Core/Strings.cs` mit DE **und** EN, Platzhalter `{0}`.
   XAML `{l:Tr Key}`, Code `Loc.T("Key", …)`. Im Code gesetzte Texte bei `Loc.Instance.LanguageChanged` neu setzen.
   Der Testlauf prüft fehlende Schlüssel.
2. **Hilfe**: Neue Funktion → Abschnitt in `de.html` **und** `en.html` (gleiche `id`). `?`-Knöpfe: `Tag="anker"`,
   Test prüft, dass jeder Anker existiert.
3. **Firma**: immer `SPÄNI Elektro-Mechanik AG` (nie „Späni“, „SPAENI“). Footer: links Firma, rechts Version.
   Entwickler nur im Info-Fenster. Support: support.elektro-spaeni.ch / support@elektro-spaeni.ch.
4. **Geheimnisse**: Passwörter nie im Klartext speichern oder loggen (`SecretStore`/DPAPI). Keine echten Kunden-,
   Geräte-, Broker- oder Hostnamen-Daten in Code, Tests, Beispielen, Screenshots, Commits (Repository ist öffentlich).
5. **Optik**: Farben nur über Brushes aus `App.xaml`; eine `PrimaryButton`-Hauptaktion pro Bereich;
   nicht zutreffende Optionen ausblenden statt nur deaktivieren.
6. **Core bleibt UI- und paketfrei**; Logik dort hinein und testen. WPF-Teil möglichst dünn.
7. Steuerelement-Namen mit Präfix: `tb_`, `bt_`, `cb_`, `chk_`, `rb_`, `mi_`, `dg_`, `tc_`, `ti_`, `bd_`, `hl_`.
8. Änderungen in `CHANGELOG.md` unter *Unveröffentlicht* eintragen.

## Release

`<Version>` in `Directory.Build.props` erhöhen → `CHANGELOG.md`-Abschnitt `## [x.y.z] - Datum` →
commit/push → `git tag vx.y.z && git push origin vx.y.z`. Der Workflow bricht ab, wenn Tag ≠ Version.
Slash-Befehl: `/release x.y.z`.

## Offene Punkte (vor dem ersten öffentlichen Release)

- [ ] `OWNER` durch den echten GitHub-Organisationsnamen ersetzen: `Directory.Build.props` (`GitHubRepository`) und `README.md`.
      Solange `OWNER/…` drinsteht, ist die Update-Prüfung abgeschaltet (`UpdateChecker.IsConfigured`).
- [ ] WPF-Build unter Windows verifizieren (wurde nur ausserhalb von Windows geschrieben; Kernlogik ist getestet).
      Insbesondere: DPAPI-Verschlüsselung (`settings.json` darf nur `"PasswordProtected": "dpapi:…"` enthalten),
      Update-Banner, Info-Fenster, Sprachwechsel.
- [ ] Offizielles Icon statt Platzhalter `src/D2cDataUpload.App/Assets/icon.ico` (+ `icon.png`).
- [ ] Screenshot `docs/screenshot.png` mit den Beispieldateien aus `docs/examples` (keine Kundendaten!) und im README einbinden.
- [ ] Optional Code-Signing-Secrets `SIGN_PFX_BASE64`, `SIGN_PFX_PASSWORD` im Repository anlegen (sonst SmartScreen-Warnung).
- [ ] Freigabe der Geschäftsleitung für die Veröffentlichung unter MIT.
