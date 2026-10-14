# Sicherheit / Security

Bitte melden Sie Sicherheitslücken **nicht** über öffentliche GitHub-Issues, sondern per E-Mail an
**support@elektro-spaeni.ch** (Betreff: „Security d2c DataUpload MQTT“). Wir melden uns so rasch wie möglich.

Please do **not** report security issues via public GitHub issues. Send an e-mail to
**support@elektro-spaeni.ch** (subject "Security d2c DataUpload MQTT").

## Hinweise zum Programm

- Das Broker-Passwort wird mit Windows-DPAPI verschlüsselt (an Windows-Benutzer und PC gebunden) gespeichert.
- Die Hilfe wird von einem lokalen Webserver ausschliesslich auf `127.0.0.1` ausgeliefert.
- Die Update-Prüfung fragt nur `api.github.com` nach der neuesten Version; es werden keine Daten übertragen.
- „Zertifikat nicht prüfen“ (TLS) ist nur für Tests gedacht.
