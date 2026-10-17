# ErgoStravaLogger – Daum Premium 8i → Strava

[Quellcode](https://github.com/compunited/ErgoStravaLogger) · [Build und Tests](https://github.com/compunited/ErgoStravaLogger/actions) · [Downloads und Releases](https://github.com/compunited/ErgoStravaLogger/releases)

Ein selbst gehosteter Trainingslogger mit kleinem Webinterface für den Daum Premium 8i. Das Ergometer ist per LAN erreichbar; die Anwendung verbindet sich direkt zum Cockpit und läuft unter Windows, Linux oder Docker. Für die LAN-Verbindung wird kein zusätzlicher Sensoradapter benötigt. Die Anwendung liest Messwerte und kann auf Wunsch Zwift die Belastung steuern lassen. Mit ausgeschalteter Steuerung stellst du das Trainingsprogramm und die Belastung am Bike ein.

Das Webinterface zeigt Verbindung, aktuelle Leistung, Kadenz, Puls, Geschwindigkeit, Distanz, letzte Fehler und gespeicherte Trainings. Strava lässt sich dort einrichten und verbinden. Aufzeichnungen starten beim Treten und pausieren standardmäßig nach 15 Sekunden ohne Kadenz. Während der Pause werden keine weiteren Messpunkte gespeichert und die Oberfläche zeigt „Training pausiert“. Solange der Distanzzähler nicht zurückgesetzt wird, setzt erneutes Treten dasselbe Training fort, auch nach längeren Pausen. Fällt die vom Cockpit gemeldete Distanz unter den vorherigen Wert, wird das bisherige Training abgeschlossen und ein neues beginnt beim nächsten Treten. Die Pausenzeit wird im Export ausgelassen. Solange die Kadenz null ist, startet keine neue Aufzeichnung, auch wenn das Cockpit weiterhin eine Zielleistung meldet. Aufzeichnungen unter 30 Sekunden aufgezeichneter Zeit werden als Journal behalten und nicht hochgeladen.

## Fertige Pakete starten

Auf der [Releases-Seite](https://github.com/compunited/ErgoStravaLogger/releases) stehen die veröffentlichten Pakete mit SHA-256-Prüfsummen:

| Plattform | Archiv |
| --- | --- |
| Windows x64 | `ErgoStravaLogger-vX.Y.Z-win-x64.zip` |
| Linux x64 | `ErgoStravaLogger-vX.Y.Z-linux-x64.tar.gz` |
| Linux ARM64, etwa Raspberry Pi OS mit 64 Bit | `ErgoStravaLogger-vX.Y.Z-linux-arm64.tar.gz` |

Die Pakete enthalten die .NET-Laufzeit, die Konfigurationsvorlage und den Ordner `wwwroot/` für das Webinterface. Entpacke das vollständige Archiv und wechsle in den entpackten Ordner. Kopiere `config.json` nach `config.local.json` und trage die Ergometer-IP ein. Private Zugangsdaten und Trainingsdateien gehören nicht ins Repository. Die Linux-Archive erhalten die Ausführungsrechte; falls diese beim Übertragen verloren gehen, setze sie mit `chmod +x ErgoStravaLogger start.sh` erneut.

Unter Windows:

```powershell
.\ErgoStravaLogger.exe web --config config.local.json
```

Unter Linux oder auf einem Raspberry Pi mit 64-Bit-Betriebssystem:

```sh
./ErgoStravaLogger web --config config.local.json
```

Alternativ starten `start.cmd` unter Windows und `./start.sh` unter Linux die Anwendung aus dem Paketordner. Beide reichen die angegebenen Argumente weiter, etwa `./start.sh web --config config.local.json`.

Öffne danach `http://<rechner>:8080`. Die Cockpiteinstellungen und die Strava-Einrichtung sind im folgenden Abschnitt beschrieben. Der Datenpfad wird relativ zur gewählten Konfigurationsdatei aufgelöst; die Vorlage verwendet `../data`. Wähle bei Bedarf einen eigenen beschreibbaren Ordner. Bei Updates bleiben `config.local.json` und die gespeicherten Daten erhalten.

Ein Test ohne Ergometer und Strava-Upload ist mit `simulate` möglich, etwa `./ErgoStravaLogger simulate --config config.local.json` beziehungsweise `.\ErgoStravaLogger.exe simulate --config config.local.json`. Er erzeugt synthetische Trainingsdateien unter dem konfigurierten Datenordner in `simulation/`.

## Auf einem Docker-Server starten

1. Das [Repository](https://github.com/compunited/ErgoStravaLogger) auf den Server klonen oder als Quellcodearchiv herunterladen.
2. `.env.example` nach `.env` kopieren und `ERGO_IP` auf die LAN-IP des Premium 8i setzen. Reserviere die IP im Router. Bei Bedarf `WEB_PORT` anpassen.
3. Am Cockpit: **Fernsteuerung EIN**, **Bedienung sperren AUS**, **Fernsteuerung durch Automatisch**, Kommunikationsprotokoll **Standard**. Das Bike muss vom Docker-Server unter TCP-Port **51955** erreichbar sein. Die Funktion `X70` setzt eine entsprechende Premium-Firmware voraus; nach den recherchierten Implementierungen ab 1.380.
4. Starten:

   ```sh
   docker compose up -d --build
   docker compose logs -f
   ```

   Falls das Compose-Plugin auf dem Server fehlt, startet und aktualisiert das mitgelieferte Skript denselben Dienst mit der Docker-CLI:

   ```sh
   sh deploy/update.sh
   docker logs -f ergo-strava
   ```

   Das Skript baut zuerst das neue Image und ersetzt anschließend den Container `ergo-strava`. Es berücksichtigt `CONFIG_FILE` aus `.env`; ohne diese Angabe verwendet es die Vorlage `ErgoStravaLogger/config.json`. Kann Docker den Ersatzcontainer nicht starten, startet das Skript den bisherigen Container wieder. Beim nächsten Update führst du denselben Befehl erneut aus. Bleibe für diesen Dienst bei der gewählten Startmethode: Compose und das Skript verwalten ihre Container jeweils selbst; das Skript ersetzt nur einen Container mit seiner eigenen Projektkennung.

5. Im Browser `http://<docker-server>:8080` öffnen. Die Compose-Datei verwendet auf Linux das Host-Netzwerk, damit Zwift die Bridge im Heimnetz finden kann. Benötigt werden ausgehende Verbindungen zum Bike und zu Strava; `WEB_PORT` bestimmt den Web-Port direkt auf dem Server.
6. Im Bereich **Strava einrichten** den angezeigten Callback-Host in deiner [Strava-App](https://www.strava.com/settings/api) als **Authorization Callback Domain** hinterlegen, ohne Port. Client ID und Client Secret im Webinterface speichern, dann **Mit Strava verbinden** anklicken und die Berechtigung zum Aktivitätsupload gewähren.
7. Am Bike trainieren. Das fertige Training erscheint als Indoor-Radfahrt auf Strava; zusätzlich kannst du die TCX-Datei im Webinterface herunterladen.

Die [Strava-Dokumentation](https://developers.strava.com/docs/authentication/) beschreibt die nötige App-Registrierung und OAuth-Freigabe. Für einen Reverse Proxy setze `PUBLIC_BASE_URL` in `.env` auf die vollständige Browser-Adresse, etwa `https://ergo.example.net`. Nutze für die Freigabe durchgehend denselben Host, damit der OAuth-State-Cookie zum Rücksprung passt. Dieses einfache Webinterface ist für dein vertrauenswürdiges Heimnetz ausgelegt; bei externem Zugriff gehört eine Anmeldung vor den Dienst.

Die Einstellungen liegen außerhalb des Containers im Projektordner auf dem Docker-Host. Die Konfigurationsdatei wird schreibgeschützt nach `/config/config.json` eingebunden, `data/` beschreibbar nach `/data`. Dadurch bleiben Einstellungen und Trainings beim Ersetzen oder Aktualisieren des Containers erhalten:

| Datei auf dem Host | Inhalt |
| --- | --- |
| `.env` | Server-Port, Ergometer-IP als Startwert, öffentliche Web-Adresse und Upload-Schalter |
| `ErgoStravaLogger/config.json` bzw. `config.local.json` | Grundkonfiguration; die lokale Datei kann private App-Zugangsdaten enthalten |
| `data/device-settings.json` | Im Webinterface gespeicherte Ergometer-IP |
| `data/zwift-settings.json` | Bridge- und Steuerungsschalter, Fahrergewicht, Leistungsgrenze, virtueller Gang und stabile virtuelle Gerätekennung |
| `data/strava-tokens.json` | Strava-Zugriffstoken und Refresh-Token |
| `data/strava-tokens.json.client.json` | Im Webinterface gespeicherte Strava-App-Zugangsdaten |
| weitere Dateien in `data/` | Trainingsjournale (`.jsonl`), TCX-Dateien und Strava-Verarbeitungsstatus (`.upload.json`) |

Sichere `.env`, die verwendete Konfigurationsdatei und den gesamten Ordner `data/`. Behalte diese Dateien bei einem Update bei und überschreibe die private Konfiguration nicht mit der Vorlage. Zugangsdaten werden nicht an das Dashboard zurückgegeben. Hinweise zu Zugangsschutz und vertraulichen Trainingsdaten stehen in [SECURITY.md](SECURITY.md).

Beim Umzug auf einen anderen Docker-Host genügt es, diese Dateien mitzuübertragen und `PUBLIC_BASE_URL` an die neue Browser-Adresse anzupassen. Das bestehende Strava-Pairing bleibt mit den gespeicherten Tokens erhalten; eine neue App-Registrierung oder Freigabe ist dafür nicht nötig. Für eine spätere erneute Verbindung muss die **Authorization Callback Domain** in der Strava-App zum neuen Host passen.

## Zwift-Bridge

Im Dashboard lässt sich **Zwift-Bridge aktivieren** einschalten. Die Anwendung bietet dann einen eigenen Direct-Connect-Trainer im LAN an, ohne QZ oder Bluetooth-Hardware am Server. Suche auf dem Zwift-Gerät nach **Daum 8i Bridge** (die Geräteanzeige kann eine kurze Kennung anhängen) und verbinde sie als Leistungssensor, Kadenzquelle und bei vorhandenem gültigem Puls als Herzfrequenzquelle. Zwift-Gerät und Server müssen sich im selben Heimnetz/Subnetz befinden; die LAN-Erkennung benötigt mDNS.

Die Bridge überträgt **Watt, Kadenz und Puls**. Ohne aktuelle Gerätedaten überträgt sie keine alten Leistungswerte, beim Stillstand 0 Watt. Der Dashboardstatus zeigt einen aktiven Dienst, verbundene Netzwerkclients und Fehler; eine Anzeige „Wartet auf Zwift“ bestätigt noch keine erfolgreiche Kopplung mit deinem Tablet.

Wenn das Daum-Cockpit eine aktive Gangschaltung meldet, übernimmt die Bridge im Streckenmodus auch Änderungen über dessen Drehknopf beziehungsweise Gangtasten. Die Diagnose zeigt den nativen Cockpit-Gang; der virtuelle Gang ändert sich um dieselbe Anzahl. Der erste Gerätewert nach Start oder Verbindungsunterbrechung verändert die gespeicherte Übersetzung nicht. Wenn keine native Schaltung gemeldet wird, halte kurz an und verwende **Cockpit-Schaltung aktivieren**. Der Button setzt den nativen Gang und bestätigt anschließend die Mindestlast. Die Web-Gänge bleiben ebenfalls verfügbar. ERG bleibt von beiden Schaltwegen unabhängig.

Für automatische Belastung **Zwift steuert Widerstand** separat einschalten und die Bridge in Zwift zusätzlich unter **Widerstand** koppeln. Die Steuerung ist standardmäßig aus. **ERG** sendet die von Zwift gewünschte Wattzahl an das Cockpit; die Gangwahl verändert sie nicht. Für **Streckenfahrten** berechnet die Bridge eine Wattvorgabe aus Zwift-Steigung, Roll- und Windwiderstand sowie Kadenz und virtuellem Gang. Die vom Daum berechnete Geschwindigkeit wird dafür nicht verwendet. Mit **− / +** im Dashboard wählst du einen leichteren oder schwereren Gang (1–24, Standard 12); so lässt sich eine hohe Kadenz auch am Anstieg beibehalten. Lastanstiege werden auf 20 W pro Sekunde geglättet. Fahrergewicht plus 10 kg für ein virtuelles Fahrrad bestimmen den Gewichtsanteil. Abfahrten werden durch die Mindestleistung des Daum begrenzt. Fahrergewicht (30–250 kg) und Leistungsgrenze (20–1000 W, Standard 300 W) lassen sich auch bei ausgeschalteter Steuerung speichern. Das Dashboard zeigt Modus, Gang, Steigung, Modellgeschwindigkeit, bestätigte Sollleistung und Steuerfehler. Web-Gänge benötigen keine Kopplung unter „Controls“ in Zwift; natives Zwift-Schalten ist hier nicht implementiert.

Die Schalterzustände, das Fahrergewicht, die Leistungsgrenze, der virtuelle Gang und eine stabile virtuelle Gerätekennung liegen in `data/zwift-settings.json`. Beim Ausschalten der Bridge werden die Netzwerkverbindungen geschlossen und die Geräteankündigung zurückgezogen. TCP-Port **36866** und UDP-Port **5353** dienen der Bridge. Die mitgelieferte Compose-Datei ist für einen Linux-Docker-Server mit `network_mode: host` eingerichtet. Auf Docker Desktop braucht die LAN-/Multicast-Erreichbarkeit einen gesonderten Test; die direkt unter Windows gestartete Anwendung bietet dieselbe Bridge an.

Wenn du Zwift selbst für den Strava-Upload nutzt, kannst du mit `STRAVA_ENABLED=false` in `.env` den automatischen Upload dieses Loggers ausschalten. Lokale Trainingsdateien werden weiterhin gespeichert. Die Umsetzung und ihre Grenzen sind in [docs/zwift-bridge.md](docs/zwift-bridge.md) beschrieben. Kopplung und Widerstandssteuerung müssen mit der jeweiligen Kombination aus Cockpit, Firmware und Zwift-Gerät geprüft werden.

## Verhalten bei Unterbrechungen

Messpunkte werden laufend in ein Journal geschrieben. Beim Neustart werden noch nicht exportierte Journale wiederhergestellt; eine unvollständige letzte Zeile wird übersprungen. Netzwerkfehler lösen einen erneuten Verbindungsversuch aus. Nach 15 Sekunden ohne neue Kadenz pausiert die Aufzeichnung. Bei unverändertem oder steigendem Distanzzähler gehört die Fortsetzung zum bisherigen Training; ein zurückgesetzter Zähler beendet es. Auch beim Beenden des Dienstes oder einem Wechsel der Ergometer-IP wird die offene Sitzung abgeschlossen.

Uploads laufen getrennt von der Geräteabfrage. Die Anwendung erzeugt echtes TCX mit UTC-Zeitstempeln, Puls, Kadenz, Watt und Geschwindigkeit und sendet es als `tcx`, Indoor-Radfahrt, an Strava. Strava verarbeitet Uploads asynchron; die Upload-ID wird vor dem Polling gespeichert und nach einem Neustart weiter abgefragt. Fehlgeschlagene Uploads behalten ihre Datei. Nach erfolgreichem Upload verhindert die gespeicherte Aktivitäts-ID erneutes Einreichen. Bei einem Verbindungsabbruch unmittelbar nach Annahme des POST, bevor die Upload-ID empfangen wurde, kann eine Wiederholung von Strava als Duplikat abgelehnt werden; die Datei bleibt erhalten.

Distanz wird relativ zum ersten Messpunkt der Sitzung aufgezeichnet. Ein zurückgesetzter Cockpit-Zähler trennt zwei Trainings; jedes hat eine eigene Distanzbasis. Längere Pausen trennen im TCX die aufgezeichneten Abschnitte und zählen nicht zur Trainingsdauer oder zur mittleren Leistung. Zeiten im Export kommen vom Logger, weshalb dessen Systemuhr stimmen sollte.

## Konfiguration und Diagnose

Die Ergometer-IP lässt sich im Webinterface beim Gerätestatus über **IP speichern** ändern. Erwartet wird eine IPv4-Adresse. Die neue Adresse wird in `data/device-settings.json` gespeichert, hat beim Start Vorrang vor `ErgoIp` aus Konfiguration und `ERGO_IP` aus der Umgebung und gilt sofort. Damit funktioniert die Einstellung auch bei einer schreibgeschützt eingebundenen Docker-Konfiguration. Beim Wechsel wird eine laufende Sitzung abgeschlossen, die bisherige Verbindung getrennt und die Anzeige alter Messwerte geleert. Das Speichern derselben IP unterbricht die Aufzeichnung nicht.

`ErgoStravaLogger/config.json` ist eine Vorlage ohne Geheimnisse. Lokal bevorzugt die Anwendung `config.local.json`. Private Konfiguration, `.env` und der Datenordner sind von Git und dem Docker-Build ausgeschlossen.

Docker Compose nutzt standardmäßig die Vorlage und die Zugangsdaten aus dem Web-Pairing in `data/`. Mit `CONFIG_FILE=./ErgoStravaLogger/config.local.json` in `.env` wählst du eine private Konfigurationsdatei. Kopiere beim Umzug auch diese Datei und `data/` auf den Server. Wenn noch keine Tokens vorhanden sind, richte die App im Webinterface ein und erteile die Strava-Freigabe im Browser.

| Einstellung | Standard | Bedeutung |
| --- | --- | --- |
| `ErgoIp`, `ErgoPort` | `192.168.0.50`, `51955` | Cockpit im LAN |
| `LoggingIntervalMs` | `1000` | Abstand zwischen erfolgreichen Abfragen |
| `TimeoutMs` | `5000` | Gesamtes Zeitlimit einer Transaktion einschließlich Verbindung |
| `ReconnectDelaySeconds` | `5` | Pause nach Kommunikationsfehlern |
| `PauseSeconds` | `15` | Aufzeichnung pausieren nach letzter Pedalbewegung |
| `IdleSeconds` | `0` | Zeitgesteuertes Trainingsende aus; positive Werte aktivieren optional eine zusätzliche Zeitgrenze |
| `MinimumTrainingSeconds` | `30` | Mindestdauer für TCX und Upload |
| `SpeedUnit` | `m/s` | Rohgeschwindigkeit; Hersteller-DLL und Vergleichscode belegen m/s |
| `SynchronizeOnConnect` | `true` | Vier ACKs zur Synchronisierung, wie in der Herstelleranwendung |
| `TraceProtocol` | `false` | Hex-Frames auf stderr für Protokolldiagnose |
| `RecordingEnabled` | `true` | Hintergrundaufzeichnung aktiv |
| `Strava.Enabled` | `true` in der Vorlage | Automatische Uploads aktiv |

Die ältere Protokollbeschreibung nennt für Geschwindigkeit km/h und RAW-Statusbytes; eine separat untersuchte Hersteller-DLL verwendet m/s und numerische ASCII-Statusfelder. Der Parser berücksichtigt beide Statusvarianten; bei nachweislich anderer Geschwindigkeitsdarstellung kann `SpeedUnit` auf `km/h` gesetzt werden. Belege, Dateihashes und Analyseadressen stehen in [docs/daum-protocol.md](docs/daum-protocol.md). Herstellerdateien werden nicht mit diesem Projekt ausgeliefert.

Der Button **Gerät diagnostizieren** fragt die Softwareversion und einen Datensatz über dieselbe Verbindung wie die Aufzeichnung ab. Ohne Bike-Verbindung zeigt das Dashboard den konkreten Fehler und den letzten erfolgreichen Messzeitpunkt. Mit `RecordingEnabled=false` und dem Diagnosebutton kannst du zunächst einen einzelnen Lesetest durchführen.

Umgebungsvariablen: `ERGO_CONFIG`, `ERGO_IP`, `OUTPUT_DIRECTORY`, `LISTEN_URL`, `PUBLIC_BASE_URL`, `STRAVA_ENABLED`, `STRAVA_CLIENT_ID`, `STRAVA_CLIENT_SECRET`, `STRAVA_REFRESH_TOKEN`, `STRAVA_ACCESS_TOKEN`. Relative Datenpfade beziehen sich auf die gewählte Konfigurationsdatei.

## Aus dem Quellcode bauen

Voraussetzung ist das .NET 10 SDK. Quellcode holen und aus seinem Hauptordner bauen:

```sh
git clone https://github.com/compunited/ErgoStravaLogger.git
cd ErgoStravaLogger
dotnet build ErgoStravaLogger.sln -c Release
dotnet run --project tests/ErgoStravaLogger.Tests.csproj -c Release
dotnet run --project ErgoStravaLogger -- simulate
```

Für den Webbetrieb kopiere `ErgoStravaLogger/config.json` nach `ErgoStravaLogger/config.local.json`, trage die Geräteadresse ein und starte:

```sh
dotnet run --project ErgoStravaLogger
```

`dotnet run --project ErgoStravaLogger -- diagnose` führt einen einzelnen Lesetest am konfigurierten Ergometer aus.

`simulate` erzeugt eine TCX-Datei im konfigurierten Datenordner unter `simulation/`, fragt kein Gerät ab und lädt nichts hoch. Weitere Befehle: `record` ohne Webinterface, `upload-pending`, `auth-url`, `exchange-code <code>`. `--config` akzeptiert einen Dateipfad (bei `dotnet run` ist das Projektverzeichnis das Arbeitsverzeichnis); `--no-device` startet das Webinterface ohne automatische Geräteabfragen, `--duration <Sekunden>` begrenzt die Laufzeit. `--help` zeigt die Befehlsübersicht. Gespeicherte Strava-Tokens und Upload-Einstellungen bleiben auch bei `--no-device` wirksam.

Auf Raspberry Pi OS mit 64 Bit kann derselbe Docker-Aufbau verwendet werden. Alternativ:

```sh
dotnet publish ErgoStravaLogger -c Release -r linux-arm64 --self-contained true -o publish/raspi
```

Kopiere den Inhalt von `publish/raspi` nach `/opt/ergo-strava`, passe dort `config.json` an, erstelle einen beschreibbaren Datenordner für den Benutzer `ergo` und installiere bei gewünschtem Autostart die Vorlage [deploy/ergo-strava.service](deploy/ergo-strava.service). Die Microsoft-.NET-Images unterstützen [ARM64 und x64](https://github.com/dotnet/dotnet-docker/blob/main/documentation/supported-platforms.md).

## Verifikation und Grenzen

Die automatisierten Tests benötigen keine NuGet-Testframeworks und prüfen bytegenaue Protokollframes, Prüfsummen, fragmentierte TCP-Antworten, ACK/NAK-Wiederholung, Zeitlimits, Wiederverbindung, TCX, Tokenrotation, asynchrone Strava-Verarbeitung, Trainingswiederherstellung, persistente Einstellungen, die Direct-Connect-Bridge und den lokalen Webserver mit OAuth-Rücksprung, Setup und Downloads. Alle Geräte- und Strava-API-Antworten in den Tests sind simuliert. Ergebnisse für den jeweiligen Quellstand stehen unter [GitHub Actions](https://github.com/compunited/ErgoStravaLogger/actions); veröffentlichte Pakete unter [Releases](https://github.com/compunited/ErgoStravaLogger/releases).

Die Tests ersetzen keine Prüfung am eigenen Ergometer. Besonders ERG-/Streckensteuerung, Cockpit-Schaltung, Zwift-Kopplung und ein vollständiger Strava-Upload benötigen einen Praxistest mit der jeweiligen Installation. Das Projekt ist unabhängig von Daum, Strava und Zwift; eine Herstellerfreigabe oder Zwift-Zertifizierung wird nicht behauptet.

## Lizenz

Der Projektquellcode steht unter der [MIT-Lizenz](LICENSE). Mitgelieferte Laufzeitkomponenten behalten ihre eigenen Lizenzbedingungen und Hinweise; siehe [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Protokollreferenzen sind in [docs/daum-protocol.md](docs/daum-protocol.md) und [docs/zwift-bridge.md](docs/zwift-bridge.md) verlinkt; Herstellerprogramme und -bibliotheken sind nicht Bestandteil dieser Veröffentlichung.
