# Sicherheit und Datenschutz

## Betrieb im vertrauenswürdigen Netz

Das Webinterface besitzt keine eigene Benutzeranmeldung. In der Standardkonfiguration lauscht es über HTTP auf allen Netzwerkschnittstellen. Jeder erreichbare Client kann Live-Messwerte und Aufzeichnungen abrufen, die Gerätekonfiguration und Strava-App-Einstellungen ändern und die angebotenen Steuerfunktionen bedienen. Der Schutz gegen fremde Browser-POST-Anfragen ersetzt keine Anmeldung oder Netzwerkzugangskontrolle.

Betreibe den Dienst in einem vertrauenswürdigen, durch eine Firewall begrenzten Heimnetz. Richte keine öffentliche Portweiterleitung ein. Für Zugriffe über nicht vertrauenswürdige Netze ist ein vorgeschalteter HTTPS-Proxy mit Anmeldung erforderlich, der auch `/api` und `/auth` schützt; der direkte Dienstport darf diesen Schutz nicht umgehen. Setze `PUBLIC_BASE_URL` auf die feste externe Browser-Adresse. Die Direct-Connect-Bridge und ihre Gerätesuche sind ebenfalls für das lokale Netz ausgelegt und besitzen keine eigene Benutzeranmeldung.

Wenn Widerstandssteuerung aktiviert ist, können verbundene Clients physische Wattvorgaben auslösen. Verwende die Steuerung nur mit passenden Leistungsgrenzen und behalte den Zugriff auf die Bedienung am Ergometer. Die Software ersetzt keine Sicherheitsfunktionen des Geräts.

## Zugangsdaten und lokale Dateien

Strava Client Secret sowie Access- und Refresh-Token sind vertraulich. Die Anwendung speichert OAuth-Tokens in der konfigurierten `Strava.TokenFile` und die App-Zugangsdaten in der danebenliegenden Datei mit der zusätzlichen Endung `.client.json`. Diese Dateien sind nicht verschlüsselt. Neue vertrauliche Dateien werden unter Unix nur für den Eigentümer lesbar und schreibbar angelegt; unter Windows gelten die geerbten Dateiberechtigungen. Schütze Datenverzeichnis, Konfiguration, `.env`, Sicherungskopien und Terminal-/Containerprotokolle entsprechend.

`device-settings.json` enthält die lokale Geräteadresse. `zwift-settings.json` enthält unter anderem die virtuelle Bridge-Identität und das eingestellte Fahrergewicht. Konfigurationsdateien können weitere private Netzwerkinformationen enthalten. Committe diese Laufzeitdateien nicht. Bei abweichenden Dateinamen und Ausgabeordnern müssen auch diese von Git und vom Docker-Build-Kontext ausgeschlossen werden. Ignore-Regeln schützen keine bereits verfolgten Dateien.

Nach einer Offenlegung widerrufe die betroffenen Strava-Zugriffsrechte bzw. erneuere die App-Zugangsdaten. Das Löschen einer Datei aus dem aktuellen Quellstand entfernt keine Kopien aus Git-Historie, Pull Requests, Releases, Container-Images oder Sicherungen.

## Trainingsdaten und Uploads

JSONL-Journale, verworfene Kurzaufzeichnungen und TCX-Dateien enthalten Zeitstempel, Herzfrequenz, Leistung, Kadenz, Geschwindigkeit und Distanz. Auch ohne GPS sind das persönliche Trainingsdaten. Upload-Sidecars enthalten Strava-Aktivitäts- und Upload-IDs. Konsolenprotokolle können Geräteadressen, Dateinamen, Trainingszeiten, Aktivitäts-IDs und Fehlermeldungen enthalten. `TraceProtocol` kann zusätzlich rohe Messdaten protokollieren.

Die Anwendung bietet lokale TCX-Downloads an. Bei eingerichteter Strava-Freigabe und aktiviertem Upload sendet sie Aufzeichnungen an Strava. Prüfe die Freigabe und deine Strava-Sichtbarkeitseinstellungen vor dem ersten Upload. Die lokale Aufzeichnung bleibt unabhängig vom Upload bestehen; beziehe alle Daten und Sicherungen in deine Lösch- und Aufbewahrungsentscheidung ein.

Verwende für Issues, Screenshots und Testfälle ausschließlich synthetische Daten. Entferne vor einer Weitergabe Zugangsdaten, private Geräteadressen, Bridge-Identitäten, Aktivitäts-IDs und persönliche Trainingswerte. Die mitgelieferten Tests verwenden erfundene Zugangsdaten und konstruierte Messwerte.

## Sicherheitslücke melden

Nutze auf GitHub **Security → Advisories → Report a vulnerability**, wenn private Meldungen verfügbar sind. Andernfalls bitte in einem öffentlichen Issue lediglich um einen privaten Kontaktweg. Veröffentliche dabei keine Zugangsdaten, echten Trainingsdateien oder ausnutzbaren Details.

Sicherheitskorrekturen werden auf dem aktuellen Standardbranch bereitgestellt. Nutze den aktuellen veröffentlichten Stand und halte die verwendete .NET-Laufzeit bzw. das Container-Basisimage aktuell.
