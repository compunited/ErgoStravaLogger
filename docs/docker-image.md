# ErgoStravaLogger mit Docker starten

Diese Anleitung gilt für `ErgoStravaLogger-v1.0.0-docker.zip` aus den [GitHub-Releases](https://github.com/compunited/ErgoStravaLogger/releases). Es enthält `compose.yaml`, `config.json` und `.env.example`. Die Anwendung wird als fertiges Image von `ghcr.io/compunited/ergostravalogger:v1.0.0` geladen; auf dem Server sind weder Quellcode-Build noch .NET-SDK nötig.

Voraussetzung sind Docker Engine und das Docker-Compose-Plugin auf Linux x64 oder Linux ARM64, etwa Raspberry Pi OS mit 64 Bit. Docker wählt die passende Image-Architektur. Der Server muss das Daum Premium 8i im LAN erreichen können. Das Webinterface besitzt keine eigene Benutzeranmeldung und gehört in ein vertrauenswürdiges Netz; für externen Zugriff ist ein vorgeschalteter Zugangsschutz erforderlich.

## Dateien vorbereiten

Entpacke das Docker-Startpaket in einen eigenen Ordner auf dem Linux-Server und wechsle dorthin. Die folgenden Befehle gelten für eine neue Installation:

```sh
cp .env.example .env
cp config.json config.local.json
mkdir -p data
chmod 600 .env config.local.json
```

Bearbeite `.env`: Trage bei `ERGO_IP` die LAN-IP des Ergometer ein und setze `CONFIG_FILE=./config.local.json`, damit die private Kopie eingebunden wird. Bei Bedarf kannst du `WEB_PORT` ändern. Die Werte in `config.local.json` enthalten weitere Einstellungen, beispielsweise Zeitlimits; die Strava-App lässt sich später im Webinterface einrichten. Trage keine privaten Zugangsdaten in öffentliche Dateien oder GitHub-Issues ein.

Die Compose-Datei bindet die Konfiguration schreibgeschützt nach `/config/config.json` ein. Im Ordner `data/` bleiben Aufzeichnungen, Strava-Tokens und im Webinterface gespeicherte Einstellungen beim Ersetzen des Containers erhalten. Beim Aktualisieren einer bestehenden Installation behältst du `.env`, `config.local.json` und `data/` bei.

## Image laden und starten

```sh
docker compose pull
docker compose up -d
docker compose logs -f
```

Öffne `http://<server-ip>:8080` beziehungsweise den eingestellten Web-Port. Mit `Ctrl+C` verlässt du die Loganzeige; der Container läuft weiter. `docker compose ps` zeigt seinen Status. Zum Beenden dient `docker compose down`; der eingebundene Datenordner bleibt bestehen.

Das Startpaket verwendet standardmäßig das feste Image-Tag `v1.0.0`. So führt ein erneutes Starten nicht automatisch zu einem Wechsel auf eine andere Version. Die [Releases-Seite](https://github.com/compunited/ErgoStravaLogger/releases) nennt die verfügbaren Versionen. Für ein Update sicherst du die lokale Konfiguration und `data/`, änderst `ERGO_IMAGE_TAG` in `.env` auf das gewünschte veröffentlichte Tag und führst erneut `docker compose pull` sowie `docker compose up -d` aus. Das zusätzliche Tag `latest` folgt stabilen Releases; ein konkretes Versionstag macht Updates nachvollziehbarer.

## Aktuelle Hauptbranche als fertiges Image

Das Tag **`main`** enthält den aktuellen getesteten Stand der Hauptbranche. GitHub veröffentlicht es automatisch nach erfolgreichen Tests unter Windows und Linux sowie Offline-Tests der Docker-Images für **Linux amd64 und ARM64**. Es benötigt keinen Release-Tag und lässt sich ohne Anmeldung herunterladen:

```sh
docker pull ghcr.io/compunited/ergostravalogger:main
```

Der Download allein ersetzt einen laufenden Container noch nicht. Für die Compose-Installation setzt du in der vorhandenen `.env`:

```dotenv
ERGO_IMAGE_TAG=main
```

Danach aktualisieren `docker compose pull` und `docker compose up -d` den Dienst. Wiederhole diese beiden Befehle für spätere Hauptbranch-Updates. Die lokalen Einstellungen und Trainings bleiben in den eingebundenen Host-Dateien erhalten. Das zusätzliche Tag `sha-<vollständiger Commit-SHA>` bezeichnet den zugehörigen veröffentlichten Commit; `latest` folgt weiterhin stabilen Releases.

## Bestehenden Docker-CLI-Host aktualisieren

Wenn der Dienst bereits mit `deploy/update.sh` eingerichtet wurde, kann derselbe Helfer ein fertiges Image verwenden. Auf dem bisherigen Host mit dem Projektordner `~/ergo-strava`:

```sh
cd ~/ergo-strava
curl -fsSL https://raw.githubusercontent.com/compunited/ErgoStravaLogger/main/deploy/update.sh -o deploy/update.sh
sh deploy/update.sh --image ghcr.io/compunited/ergostravalogger:main
docker logs -f ergo-strava
```

Das funktioniert ohne Compose-Plugin und ohne lokalen Quellcode-Build. Der Helfer lädt das gewählte Image zuerst und ersetzt danach den von ihm verwalteten Container. `.env`, die dort unter `CONFIG_FILE` gewählte Konfiguration und `data/` werden weiterhin vom Host eingebunden. Kann Docker den Ersatzcontainer nicht starten, stellt der Helfer den bisherigen Container wieder her. Bei weiteren Updates genügt derselbe `sh deploy/update.sh --image ...`-Befehl. Bleibe bei der bisherigen Verwaltung mit dem Helfer; einen durch Compose verwalteten Container ersetzt du mit Compose.

## Cockpit und Strava einrichten

Am Premium 8i müssen **Fernsteuerung EIN**, **Bedienung sperren AUS**, **Fernsteuerung durch Automatisch** und Kommunikationsprotokoll **Standard** eingestellt sein. Der Server benötigt Zugriff auf TCP-Port **51955** des Bike. Die verwendete Trainingsdatenabfrage `X70` setzt passende Firmware voraus; die Protokollreferenzen nennen dafür Version 1.380 oder neuer.

Prüfe im Dashboard die Geräteadresse und starte **Gerät diagnostizieren**. Für Strava hinterlegst du den dort angezeigten Callback-Host in deiner [Strava-App](https://www.strava.com/settings/api) als **Authorization Callback Domain**, ohne Port. Speichere Client ID und Client Secret im Webinterface und erteile über **Mit Strava verbinden** die Freigabe zum Aktivitätsupload. Für einen Reverse Proxy setzt du `PUBLIC_BASE_URL` in `.env` auf die tatsächliche Browser-Adresse und verwendest denselben Host während der gesamten Anmeldung.

Mit `STRAVA_ENABLED=false` in `.env` lässt sich der automatische Strava-Upload abschalten, etwa wenn Zwift diesen übernimmt. Lokale Trainingsdateien werden weiterhin gespeichert.

## Netzwerk und Zwift

Das Startpaket verwendet `network_mode: host`, damit die Zwift-Bridge Multicast im Heimnetz nutzen kann. Der Web-Port wird deshalb unmittelbar auf dem Linux-Server geöffnet. Für die optionale Bridge müssen Server und Zwift-Gerät im selben erreichbaren Subnetz liegen; benötigt werden UDP **5353** für mDNS und TCP **36866** für die Direct-Connect-Verbindung. Eine Router-Portweiterleitung ins Internet ist nicht nötig.

Die Bridge und die Widerstandssteuerung sind zunächst ausgeschaltet. Aktiviere sie bei Bedarf getrennt im Dashboard. Prüfe Kopplung, ERG-/Streckensteuerung und Cockpit-Schaltung am eigenen Gerät. Simulierte Tests ersetzen diese Hardwareprüfung und einen vollständigen Strava-Upload nicht. Docker Desktop benötigt einen gesonderten Test seiner LAN-/Multicast-Erreichbarkeit.

Weitere Informationen: [vollständige Anleitung](https://github.com/compunited/ErgoStravaLogger/blob/main/README.md), [Zwift-Bridge](https://github.com/compunited/ErgoStravaLogger/blob/main/docs/zwift-bridge.md), [Sicherheit und Datenschutz](https://github.com/compunited/ErgoStravaLogger/blob/main/SECURITY.md), [MIT-Lizenz](https://github.com/compunited/ErgoStravaLogger/blob/main/LICENSE) und [Hinweise zu Drittkomponenten](https://github.com/compunited/ErgoStravaLogger/blob/main/THIRD-PARTY-NOTICES.md).
