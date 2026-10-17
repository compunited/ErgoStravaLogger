# Zwift Bridge für den Daum premium 8i

Die integrierte Bridge stellt die Messwerte des Daum als Netzwerksensor bereit und kann dessen Wattvorgabe steuern. Dafür werden weder QZ noch Bluetooth-/ANT+-Adapter benötigt: Das Bike ist per LAN mit dem Heimnetz verbunden; ErgoStravaLogger und das Zwift-Gerät müssen im selben erreichbaren Netz liegen.

```text
Daum premium 8i ── LAN ── ErgoStravaLogger ── LAN/WLAN ── Zwift auf Android
                         │
                         └── Aufzeichnung und Strava-Upload
```

Zwift hat Direct Connect unter Android im [Direct-Connect-Update vom April 2022](https://forums.zwift.com/t/wahoo-kickr-direct-connect-update-april-2022/582431) beschrieben. Die Daum-Bridge ist eine unabhängige Protokollimplementierung. Kopplung und Widerstandssteuerung müssen mit der verwendeten Zwift-Version und dem echten Bike geprüft werden. Eine Zwift-Zertifizierung wird nicht behauptet.

## Einschalten und verbinden

1. Im Webinterface die IP-Adresse des Ergometer prüfen. Die Diagnose muss aktuelle Messwerte anzeigen.
2. **„Zwift Bridge aktivieren“** einschalten. Die Einstellung wird gespeichert und gilt nach einem Neustart weiter.
3. Auf dem Tablet Zwift öffnen und auf dem Pairing-Bildschirm nach einer Leistungsquelle suchen. Die Bridge meldet sich als **„Daum 8i Bridge“** mit einem kurzen Identitäts-Suffix, beispielsweise `Daum 8i Bridge 1234`.
4. Dieselbe Bridge für die Kadenz auswählen. Wenn das Bike einen gültigen Pulswert liefert, kann die Bridge auch als Herzfrequenzsensor gekoppelt werden.

Es werden Watt, Kadenz und vorhandener Puls übertragen. Bei fehlenden oder veralteten Gerätedaten meldet die Diagnose, dass keine aktuellen Daten verfügbar sind, und die Bridge verwendet keine alten Leistungswerte weiter.

## Widerstand durch Zwift steuern

Die zusätzliche Steuerung ist standardmäßig ausgeschaltet. Schalte im Dashboard **Zwift steuert Widerstand** ein und kopple die Bridge in Zwift zusätzlich unter **Widerstand**. Nach dem Umschalten kann eine erneute Suche oder Kopplung nötig sein, damit Zwift die geänderten Trainerfunktionen einliest. Fahrergewicht und Leistungsgrenze werden mit dem Schalter in `data/zwift-settings.json` auf dem Host gespeichert. Bei ausgeschalteter Steuerung wählst du die Belastung am Daum-Cockpit.

| Einstellung | Wirkung |
| --- | --- |
| Fahrergewicht | 30–250 kg, standardmäßig 75 kg; für die Berechnung der Streckenlast |
| Leistungsgrenze | 20–1000 W, standardmäßig 300 W; zusätzlich gelten die vom Cockpit gemeldeten Grenzen |
| ERG | Zwift übermittelt eine Wattvorgabe, die das Cockpit als Zielleistung erhält; Vorgaben außerhalb der erlaubten Grenzen werden abgelehnt |
| Strecke | Zwift übermittelt Steigung, Wind und Rollwiderstand; die Bridge berechnet mit Kadenz und virtuellem Gang eine Wattvorgabe und begrenzt sie auf den erlaubten Bereich |
| Virtueller Gang | 1–24, standardmäßig 12; **−** macht die Last bei gleicher Kadenz leichter, **+** schwerer. Änderungen gelten beim nächsten Messwert, bleiben gespeichert und unterbrechen die Verbindung nicht. ERG bleibt unverändert |

Die Streckenberechnung verwendet eine unabhängige virtuelle Übersetzung: Gang 12 entspricht 4,9 m pro Kurbelumdrehung, benachbarte Gänge unterscheiden sich um 15 %. Aus Gang und Kadenz entsteht eine Modellgeschwindigkeit. Die vom Daum berechnete Geschwindigkeit geht nicht in die Lastberechnung ein, weil das eine Rückkopplung zwischen Wattvorgabe und Gerätegeschwindigkeit erzeugen kann. Gerätewerte für Aufzeichnung und Sensoren bleiben erhalten. Zwift berechnet seine Fahrgeschwindigkeit selbst.

Das Modell verwendet Fahrergewicht plus 10 kg für ein virtuelles Fahrrad und einen Antriebswirkungsgrad von 0,97. Luftkraft ist `0.5 × Cw × vRel × |vRel|`; Cw wird als Luftdichte × Luftwiderstandsbeiwert × Stirnfläche interpretiert. Das ist eine Modellannahme entsprechend [Garmins Definition des Windkoeffizienten](https://developer.garmin.com/connect-iq/api-docs/Toybox/AntPlus/SimulationSettings.html) und der [NASA-Luftwiderstandsformel](https://www1.grc.nasa.gov/beginners-guide-to-aeronautics/drag-equation/). FTMS definiert die übertragenen Parameter, aber keine verbindliche Lastformel. Steigungen und Gegenwind erhöhen die Last; zum Beibehalten einer hohen Kadenz schaltest du herunter.

Lastanstiege im Streckenmodus sind auf 20 W pro Sekunde begrenzt. Lange Messwertlücken erlauben keinen größeren Sprung. Lastsenkungen, insbesondere beim Stillstand, wirken sofort. Mindestleistung und Leistungsgrenze gelten weiterhin; auf Abfahrten kann das Bike keine negative Last liefern. ERG übernimmt direkt die gewünschte Wattzahl: Mehr Kadenz bedeutet bei gleicher Leistung weniger Kraft pro Kurbelumdrehung.

Das **Controls**-Feld in Zwift ist für separate Click-, Play- oder Ride-Controller. [Zwifts natives Virtual Shifting](https://support.zwift.com/virtual-shifting-faq-r16UiRFlT) benötigt außerdem einen kompatiblen Trainer; normale FTMS-Widerstandssteuerung genügt dafür nicht. Die hier eingebauten Web-Gänge funktionieren unabhängig davon. Öffne das Dashboard auf einem zweiten Gerät oder neben Zwift auf dem Tablet.

Gangwechsel am **Daum-Cockpit** werden ebenfalls übernommen, sobald das Gerät eine aktive Gangschaltung meldet. Die Bridge liest den nativen Gang (1–28) im vorhandenen X70-Datensatz. Der erste Wert nach Start oder Verbindungsunterbrechung setzt nur den Ausgangspunkt; spätere Änderungen verschieben den virtuellen Gang um dieselbe Anzahl, begrenzt auf 1–24. Web- und Cockpit-Bedienung lassen sich so kombinieren. ERG, deaktivierte Steuerung und Stillstand ohne Streckenvorgabe übernehmen keine Gangänderung. Die Anzeige „Cockpit-Gang“ bestätigt, dass das Gerät einen Gang meldet; fehlt er, bleiben die Web-Gänge verfügbar. Beim Cockpit muss **Bedienung sperren AUS** gesetzt sein. Meldet der Daum noch keinen Gang, halte kurz an und klicke **Cockpit-Schaltung aktivieren**. Der explizite M71-Befehl setzt den nativen Gang auf die gespeicherte virtuelle Gangnummer; anschließend wird auf derselben Verbindung die Mindestlast bestätigt, auch wenn die Gangantwort fehlerhaft ist. Während des Tretens oder im ERG-Modus wird die Aktivierung abgelehnt. Die Bridge startet kein Daum-Streckenprogramm, das die Wattvorgaben überlagern könnte. Ob die Cockpit-Anzeige und deren Tasten im gewählten Gerätemodus reagieren, muss am Gerät geprüft werden.

Das Dashboard zeigt **ERG**, **Strecke** oder **Bereit**, den virtuellen Gang, die zuletzt vom Gerät bestätigte Sollleistung und Fehler. Im Streckenmodus erscheinen Steigung und Modellgeschwindigkeit; eine rechnerische Last oberhalb der Leistungsgrenze wird ausdrücklich angezeigt. Die gemessene Leistung steht weiterhin oben bei den Live-Werten. Ein Steuerfehler ist kein Nachweis, dass das Cockpit einen Befehl übernommen hat; nach Verbindungsverlust muss die Diagnose wieder aktuelle Gerätedaten zeigen.

Vor dem Angebot der Steuerfunktionen fragt die Bridge mit `L70W` die Leistungsgrenzen des Daum ab. Wattvorgaben werden als `S23` über dieselbe serialisierte Geräteverbindung wie die Messdaten gesendet. Es werden keine Trainingsstarts oder Distanz-Resets am Cockpit ausgelöst. Der Wattsetter ist in der lokalen Hersteller-DLL nachgewiesen und findet sich unabhängig davon in der [Incyclist-Premium-Implementierung](https://github.com/incyclist/devices/blob/ce018f7b269afbab8dc06a929461ed11b469da44/src/serial/daum/premium/comms.ts). Die Zwift-Seite verwendet den Fitness Machine Control Point gemäß der [Bluetooth-SIG-Spezifikation des Fitness Machine Service](https://www.bluetooth.com/specifications/specs/fitness-machine-service-1-0-1/), transportiert durch Direct Connect.

Das Ausschalten der Bridge beendet Discovery und TCP-Verbindungen. Aufzeichnung und Strava bleiben unabhängig davon verfügbar. Wenn Zwift die Fahrt ebenfalls zu Strava hochlädt, kann bei gleichzeitigem Logger-Upload eine doppelte Aktivität entstehen; entscheide für diese Fahrt, welcher Dienst den Upload übernehmen soll.

## Docker auf dem Linux-Server oder Raspberry Pi

Die mitgelieferte `compose.yaml` verwendet `network_mode: host`. Damit kann die Discovery am lokalen Multicast-Verkehr teilnehmen und die tatsächlich erreichbare IP-Adresse des Servers veröffentlichen. Server, Bike und Tablet müssen im selben Subnetz liegen. LAN für den Server und WLAN für das Tablet sind dafür geeignet, sofern der Router die Teilnehmer miteinander kommunizieren lässt.

```sh
docker compose up -d --build
```

Das Webinterface ist standardmäßig unter `http://SERVER-IP:8080` erreichbar. Für die Bridge müssen im lokalen Netz diese Verbindungen möglich sein:

| Zweck | Protokoll | Adresse/Port |
| --- | --- | --- |
| Webinterface | TCP | Server, 8080 beziehungsweise konfigurierter Web-Port |
| Gerätesuche, mDNS | UDP | 5353, Multicast `224.0.0.251` |
| Zwift-Datenverbindung | TCP | Server, 36866 beziehungsweise `Zwift.Port` |

Eine Router-Portweiterleitung ins Internet ist hierfür nicht erforderlich. Ein normales Docker-Bridge-Netz mit bloßer TCP-Portfreigabe genügt für die automatische Gerätesuche nicht: Das Tablet muss die Multicast-Ankündigung und die darin angegebene IP erreichen können.

Docker beschreibt die gemeinsame Netzwerkumgebung und die Unterschiede zu Docker Desktop in der [Dokumentation zum Host-Netzwerk](https://docs.docker.com/engine/network/drivers/host/). Empfohlen ist hier Docker Engine direkt auf Linux. Docker Desktop stellt nicht dieselben Host-Schnittstellen bereit; deshalb ist dessen Host-Modus kein Nachweis, dass die LAN-Discovery dieser Bridge funktioniert. Für einen Test auf Windows kann die Anwendung direkt auf dem Host laufen.

## Diagnose bei fehlender Erkennung

- **Bridge nicht aktiv / Fehlertext:** Die Meldung im Webinterface prüfen. Ein belegter TCP-Port, ein exklusiv belegter UDP-Port oder ein Namenskonflikt verhindern den Start. Die Bridge nach Behebung aus- und wieder einschalten.
- **Bridge aktiv, aber keine Zwift-Verbindung:** Tablet und Server im selben Subnetz prüfen. Gast-WLAN, WLAN-Client-Isolation, getrennte VLANs, VPNs und Firewalls können Discovery oder TCP blockieren. Zwift nennt entsprechende Punkte in den [Android-Direct-Connect-Hinweisen](https://forums.zwift.com/t/cant-find-wahoo-direct-connect-android-march-2022/581717).
- **Zwift verbunden, aber keine aktuellen Werte:** Ergometer-Diagnose prüfen, Bike einschalten und treten. Die Bridge stellt selbst keine zweite Daum-Verbindung her; sie verwendet den Datenstrom des Loggers.
- **Leistung gekoppelt, Widerstand nicht gefunden:** Den zusätzlichen Schalter **Zwift steuert Widerstand** und den Steuerstatus prüfen. Danach die Bridge unter **Widerstand** erneut suchen. Die Steuerfunktionen werden erst nach erfolgreicher Abfrage der Daum-Leistungsgrenzen angeboten.
- **Widerstand gekoppelt, Vorgabe abgelehnt:** Die Leistungsgrenze im Dashboard und den Steuerfehler prüfen. Ein ERG-Ziel oberhalb der eingestellten oder vom Gerät erlaubten Grenze wird abgelehnt; bei Streckenfahrten begrenzt die Bridge den berechneten Wert.
- **Mehrere Netzwerkkarten:** Die Discovery veröffentlicht je Schnittstelle deren IPv4-Adressen. Mit `Zwift.BindAddress` kann eine konkrete lokale IPv4-Adresse gewählt werden. Nach einem Wechsel der Server-IP die Bridge neu starten, damit die Schnittstellen neu eingelesen werden.
- **Mehrere Kopien der Installation:** Jede Bridge benötigt eine eigene Identität. Keine laufenden Instanzen mit kopierter Identität parallel im selben Netz verwenden. Der Logger erzeugt und speichert eine stabile Identität pro Installation.

Zur Prüfung der Discovery kann ein DNS-SD-Browser nach `_wahoo-fitness-tnp._tcp.local` suchen. Unter Linux eignet sich, sofern Avahi-Werkzeuge installiert sind:

```sh
avahi-browse -rt _wahoo-fitness-tnp._tcp
```

Der Eintrag sollte eine lokale Server-IP und den konfigurierten TCP-Port enthalten. `0.0.0.0` ist eine Bind-Adresse und wird nicht als Zieladresse angekündigt. Loopback-Adressen werden nur gewählt, wenn keine passende normale Schnittstelle verfügbar ist; dann ist die Bridge für das Tablet nicht erreichbar.

## Protokoll und Prüfungen

Die Bridge implementiert WFTNP/Direct Connect über TCP sowie IPv4 mDNS/DNS-SD mit .NET-Netzwerkfunktionen. Es werden keine QZ-Komponenten oder externen Protokollbibliotheken eingebunden. Als Protokollreferenz dient die [öffentliche Beschreibung von WFTNP](https://github.com/elfrances/wahoo-fitness-tnp). Der veröffentlichte Projektquellcode liegt unter [compunited/ErgoStravaLogger](https://github.com/compunited/ErgoStravaLogger).

Die Discovery liefert PTR-, SRV-, TXT- und A-Records. Die TXT-Daten enthalten `ble-service-uuids` als kommaseparierte 16-Bit-UUIDs, eine stabile virtuelle `mac-address` und eine virtuelle `serial-number`. Diese Identität beschreibt die Bridge. Die Angaben sind keine ausgelesene Hardware-MAC oder Seriennummer des Daum. Der SRV-Port entspricht dem tatsächlichen Listener-Port.

Beim Start werden drei Probes gesendet. Danach folgen eine Ankündigung, eine zweite nach einer Sekunde und Auffrischungen im Abstand von 60 Sekunden. Die Record-TTL beträgt 120 Sekunden; beim regulären Abschalten wird ein Goodbye mit TTL 0 gesendet. Discovery beantwortet Service-, Instanz-, Host- und DNS-SD-Browse-Anfragen sowie Unicast-Response-Anfragen. Der UDP-Socket erlaubt Port-Wiederverwendung, damit er mit anderen kooperativ bindenden mDNS-Diensten arbeiten kann.

Die Drahtformate orientieren sich an [RFC 6762 für Multicast DNS](https://datatracker.ietf.org/doc/html/rfc6762) und [RFC 6763 für DNS-SD](https://datatracker.ietf.org/doc/html/rfc6763). Der Responder ist bewusst begrenzt: IPv4, keine automatische Umbenennung bei Konflikten, kein vollständiges Probe-Tiebreak-Verfahren und keine vollständige Known-Answer-Unterdrückung. Erkannte kollidierende Host-/Instanzdaten stoppen Discovery mit einer Diagnosemeldung. IPv6, wechselnde Schnittstellen während des Betriebs und alle Varianten anderer mDNS-Responder müssen gesondert geprüft werden.

Die automatischen Tests nutzen synthetische DNS- und TCP-Daten. Für mDNS prüfen sie die Records und TXT-Werte, TTL/Goodbye, Abfragefilter, QU und Legacy-Unicast, DNS-Kompressionszeiger, begrenzte Paketgrößen sowie die Ablehnung beschädigter oder zyklisch komprimierter Pakete. Sie benötigen keinen echten Multicast-Verkehr, kein Bike und kein Strava-Konto. Diese Tests belegen die implementierten Formate; der abschließende Praxistest erfolgt mit Zwift auf dem Tablet.
