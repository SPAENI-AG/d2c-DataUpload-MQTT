# m2v / d2c DataUpload MQTT

Windows-Programm, das CSV-Dateien zeilenweise und **gedrosselt** an einen MQTT-Broker sendet –
z.B. um Messdaten nach einem Verbindungsausfall in die Cloud nachzuliefern.
Entwickelt von **SPÄNI Elektro-Mechanik AG** (device2cloud).

*English summary below.*

<!-- Screenshot einfügen: docs/screenshot.png (mit den Beispieldateien aus docs/examples, ohne Kundendaten) -->

## Funktionen

- **1 CSV-Zeile = 1 MQTT-Nachricht**, auch für sehr grosse Dateien (500'000+ Zeilen, gestreamt)
- **Spalten-Zuordnung im Programm**: Topic aus Spalte (+ Präfix) oder Vorlage `d2c/{device}/data`;
  Payload unverändert aus einer Spalte oder als JSON aus ausgewählten Spalten (Zahlen, Zeitstempel → Epoch)
- **Senderate begrenzt** (z.B. 50 Nachrichten/s), QoS 0/1/2, Fenster für unbestätigte Nachrichten
- **Kein Datenverlust**: automatischer Wiederaufbau bei Verbindungsabbruch, Fortsetzen ab der letzten bestätigten Zeile
- Pause / Stopp / Prüflauf, Fortschritt, Rate, Restzeit, Log
- TLS (mqtts) mit eigenem CA-Zertifikat, Benutzer/Passwort – **Passwort verschlüsselt gespeichert** (Windows-DPAPI)
- Oberfläche **Deutsch / English** (Umschaltung ohne Neustart), **Hilfe im Browser** (F1 und ?-Knöpfe)
- **Update-Hinweis**: das Programm meldet neue Versionen aus den GitHub-Releases

## Installation

1. Unter [**Releases**](https://github.com/OWNER/d2c-dataupload-mqtt/releases/latest) die Datei
   `d2c_DataUpload_MQTT_<version>_win-x64.zip` herunterladen.
2. In einen eigenen Ordner entpacken und `d2c_DataUpload_MQTT.exe` starten.
   Keine Installation, keine Admin-Rechte, .NET ist enthalten. Windows 10/11 x64.

**Update:** Das Programm zeigt beim Start einen Hinweis, wenn eine neue Version verfügbar ist
(höchstens einmal täglich, abschaltbar). Programm schliessen, neue ZIP in denselben Ordner entpacken.
Einstellungen bleiben erhalten (`%APPDATA%\d2c_DataUpload_MQTT`).

## Kurzanleitung

1. **Einstellungen**: Broker, Port, TLS, Benutzer/Passwort → *Verbindung testen* → *Speichern*
2. **Senden**: *CSV öffnen* → Topic- und Payload-Spalte wählen → Vorschau der ersten Nachricht prüfen
3. Optional *Prüflauf*, dann *Senden starten*. Bei Abbruch: *Fortsetzen ab Zeile …*

Beispieldateien: [`docs/examples`](docs/examples). Ausführliche Hilfe im Programm (F1).

## Entwicklung

Voraussetzungen: .NET SDK 10, Windows für die Oberfläche (WPF). Die Kernlogik läuft und testet auch unter Linux/macOS.

```
dotnet build D2cDataUpload.slnx -c Release
dotnet run --project src/D2cDataUpload.App
dotnet run --project tests/D2cDataUpload.Tests      # Broker-Tests brauchen "mosquitto" im PATH
```

| Projekt | Inhalt |
|---|---|
| `src/D2cDataUpload.Core` | Logik ohne UI und ohne NuGet-Pakete: CSV (RFC 4180), Zuordnung, Sende-Job, eigener MQTT-3.1.1-Client, Einstellungen, Passwort-Verschlüsselung, Übersetzung, Hilfe-Server, Update-Prüfung |
| `src/D2cDataUpload.App` | WPF-Oberfläche (net10.0-windows), Hilfe-Seiten `Help/`, Styles in `App.xaml` |
| `tests/D2cDataUpload.Tests` | Testlauf als Konsolenprogramm (inkl. Tests gegen lokalen Mosquitto) |

**Release erstellen:** Version in `Directory.Build.props` erhöhen, `CHANGELOG.md` ergänzen, committen,
dann `git tag v1.3.0 && git push origin v1.3.0`. GitHub Actions testet, baut die ZIP und erstellt das Release.
Details: [CONTRIBUTING.md](CONTRIBUTING.md).

## Support

- Fehler und Wünsche: [GitHub Issues](https://github.com/OWNER/d2c-dataupload-mqtt/issues)
- SPÄNI Elektro-Mechanik AG – [support.elektro-spaeni.ch](https://support.elektro-spaeni.ch) – support@elektro-spaeni.ch
- Sicherheitslücken bitte nicht öffentlich melden: siehe [SECURITY.md](SECURITY.md)

## Lizenz

[MIT](LICENSE) © SPÄNI Elektro-Mechanik AG

---

## English summary

Windows tool that sends CSV files row by row, **rate-limited**, to an MQTT broker – e.g. to upload
measurement data to the cloud after a connection outage. Column mapping for topic and payload in the UI,
QoS 0/1/2, automatic reconnect, resume from the last acknowledged row, TLS, encrypted password storage,
German/English UI, browser-based help and update notification via GitHub Releases.

**Install:** download `d2c_DataUpload_MQTT_<version>_win-x64.zip` from
[Releases](https://github.com/OWNER/d2c-dataupload-mqtt/releases/latest), extract, run `d2c_DataUpload_MQTT.exe`.
No installation required. **Build:** .NET SDK 10, `dotnet build D2cDataUpload.slnx`. **License:** MIT.
