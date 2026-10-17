# Daum Premium 8i: Protokollbefund

Die folgenden Befunde dokumentieren eine statische Untersuchung vom 12. Oktober 2026 an einer separat installierten Windows-Herstelleranwendung. Diese Untersuchung umfasste keine Geräteverbindung oder ausgeführten Steuerbefehle. Die Angaben erklären die Implementierung; sie ersetzen keinen Test mit dem eigenen Cockpit und dessen Firmware.

## Untersuchte Dateien

Die folgenden Herstellerdateien dienten ausschließlich als Analysegrundlage. Sie sind weder im öffentlichen Repository noch in dessen Paketen enthalten und werden zur Laufzeit nicht benötigt. Die Hashes identifizieren die untersuchten Versionen; für andere Versionen gelten die unten genannten Adressen nicht ohne erneute Prüfung.

| Datei | Größe | SHA-256 | Rolle |
| --- | ---: | --- | --- |
| `hpc_premium.cdl` | 468480 Bytes | `6d917fc10ac78f43ce2011f1cc1f03f5f7c1503ec12bd7064e6a8779a78a32ef` | Native Delphi-PE32-Bibliothek mit dem Premium-Kommunikationstreiber |
| `emw_dll.dll` | 204800 Bytes | `0bcf4b3d4b1a5906f2f1651b316251b571fe8687f39b42a475b578c33c1705e5` | Native MFC-Bibliothek zur Trainingskarte, Firmwaredateien und importierten Trainingsdaten |

`hpc_premium.cdl` exportiert `PDID_CreateDevice`, `PDID_DestroyDevice`, `PDID_GetDriverStruct` und `PDID_GetDriverVersion`. Es importiert `wsock32.dll` mit `socket`, `connect`, `send`, `recv`, `htons`, `closesocket`, zusätzlich serielle Win32-Funktionen. `emw_dll.dll` besitzt keine Winsock-Imports; deren `EMW_CreateTrainingData` und `EMW_ImportTrainingData` betreffen Dateien/Karten. Für Live-Daten ist der `.cdl`-Treiber maßgeblich.

Adressen unten sind virtuelle Adressen bei der bevorzugten ImageBase `0x00400000`; Dateioffsets beziehen sich auf exakt die oben gehashte Datei. Für ihre CODE-Sektion gilt `Dateioffset = VA - 0x00400C00`.

## Transport und Verbindungsbeginn

TCP-Port **51955** ist direkt belegt: VA `0x0045E611`, Dateioffset `0x0005DA11`, enthält `mov cx,0xCAF3`; die folgende Socket-Verbindungsfunktion VA `0x0045C5D4` verwendet diesen Wert über `htons` in `sockaddr_in` und ruft `connect` auf. Es handelt sich um IPv4/TCP. Der Treiber hält das Socketobjekt für mehrere Transaktionen.

Der Initialisierungsweg sendet vor der Geräteabfrage eine Synchronisierungsfolge: VA `0x0045F37E` ruft `0x00460900` auf. Diese Funktion ruft den ACK-Sender `0x00460140` viermal auf, mit drei Pausen von jeweils 30 ms. Danach folgen lesende Abfragen `Y00` und `V00`; `V70` wird separat verwendet. Dieselbe ACK-Folge wird bei bestimmten Kommunikationsfehlern wiederholt. Vier ACKs einmal nach Verbindungsaufbau bilden daher das Verhalten der vorhandenen Herstelleranwendung ab. Ein einzelnes ACK vor jeder Abfrage ist kein nachgewiesener Transaktionsablauf.

Als unabhängige Primärquelle nennt der ursprüngliche [Daum-Reverse-Engineering-Bericht von ackstorm](https://ackstorm.de/projects/daum/daum.html) ebenfalls TCP 51955. Die App-Dokumentation [Daum 8/8i von ergo.ub-online](https://ergo.ub-online.de/index.php/en/konfiguration-en/konfiguration-daum-8-8i) empfiehlt am Cockpit Fernsteuerung EIN, Bedienung sperren AUS, Fernsteuerung durch Automatisch und Kommunikationsprotokoll Standard. Diese Cockpiteinstellungen werden vom Logger nicht verändert.

## Frames und Bestätigungen

Der lokale Framekonstruktor VA `0x0045AD48`, Dateioffset `0x0005A148`, schreibt:

```text
SOH (01) | Header (3 ASCII-Zeichen) | Nutzdaten | Prüfsumme (2 ASCII-Ziffern) | ETB (17)
```

Die Prüfsummenroutine VA `0x0045ACE0`, Dateioffset `0x0005A0E0`, summiert Bytes ab Index 1, also nach SOH. Der Aufrufer übergibt Länge `3 + Nutzdatenlänge`. VA `0x0045AD1A` dividiert durch 100 (`0x64`) und verwendet den Rest. Die Formatkonstante VA `0x0045AD40` ist `%02d`. SOH, Prüfziffern und ETB gehen nicht in die Summe ein. GS (`0x1D`) und eventuelle Nutzdatenleerzeichen dagegen schon.

Eine reine Abfrage hat keine Nutzdaten und kein eingefügtes Leerzeichen:

| Abfrage | Escaped Frame | Hex |
| --- | --- | --- |
| Protokollversion | `\x01V0082\x17` | `01 56 30 30 38 32 17` |
| Softwareversion | `\x01V7089\x17` | `01 56 37 30 38 39 17` |
| Gerätetyp | `\x01Y0085\x17` | `01 59 30 30 38 35 17` |
| Gesamte Trainingsdaten | `\x01X7091\x17` | `01 58 37 30 39 31 17` |

Die normale Transaktion lautet: Client sendet Abfrage, Ergometer sendet ACK `0x06`, Ergometer sendet Antwortframe, Client prüft Frame und sendet ACK. Bei falscher Prüfsumme sendet der Client NAK `0x15` und erwartet die wiederholte Antwort. Der Antwortheader muss zum angefragten Header passen. Der lokale ACK-Sender ist VA `0x00460140`, NAK-Sender `0x00460188`. Antwortprüfung und anschließendes ACK/NAK sind im Weg `0x0045FDCC` nachzuweisen; der generische Query-Aufrufer ist `0x004601D0`.

Ein TCP-Read kann nur das ACK, einen Teil eines Frames oder ACK und vollständigen Frame liefern. Der Client muss eingehende Bytes puffern. Der lokale Decoder `0x0045ADFC` sucht ETB und validiert beide Prüfziffern. Der Treiber verwendet 1024-Byte-Puffer; dies ist eine Implementierungsgrenze, keine aus dem Gerät abgeleitete Maximalpaketlänge. Der Sendepfad wartet bis 11 s auf ACK und begrenzt Versuche auf fünf (`0x0045FC21`, `0x0045FD06`). Der Antwortpfad wartet bis 10 s (`0x00460007`) und wiederholt begrenzt. Fehlende oder defekte Daten dürfen niemals als Trainingsmessung gespeichert werden.

## X70-Trainingsdaten

Der Logger liest den nativen Gang aus Feld 10 zusätzlich als `NativeGear`: 2–29 bedeutet Gang 1–28; 0, 1 und unbekannte Werte ergeben keinen aktiven Gang. Im Zwift-Streckenmodus übernimmt die Bridge spätere Gangänderungen als relative Schaltbefehle für ihre virtuelle Übersetzung. Die erste Probe und die erste Probe nach Verbindungsunterbrechung verändern die gespeicherte Übersetzung nicht. Das Lesen benötigt keinen zusätzlichen Gerätebefehl.

Die explizite Cockpit-Aktivierung verwendet `M71` mit Gang plus 1 als ungepolstertem ASCII-Rohwert: Native Gänge 1–28 entsprechen 2–29 auf der Leitung. Für den nativen Gang 12 wird daher `M7113` gesendet; der bestätigte Rohwert wird wieder um 1 vermindert. Die Implementierung verwendet diese Verschiebung einheitlich für M71 und X70; die öffentliche API verwendet native Gangnummern. Der M71-Setter der untersuchten Herstelleranwendung formatiert den Wert als `%d`; unabhängig davon nutzt [Incyclist](https://github.com/incyclist/devices/blob/ce018f7b269afbab8dc06a929461ed11b469da44/src/serial/daum/premium/comms.ts) dasselbe Kommando. Der Logger führt diesen Befehl nur beim Anhalten, mit aktivierter Widerstandssteuerung und außerhalb von ERG aus und bestätigt danach `S23` mit der vom Gerät gemeldeten Mindestleistung. Gangdarstellung und Tastenverhalten sind am jeweiligen Cockpit zu prüfen.

Die Abfragekonstante `X70` liegt bei Dateioffset `0x0005DFBC`, VA `0x0045EBBC`. Der Parser beginnt bei VA `0x0045E930`. Seine Formatkonstante liegt bei Dateioffset `0x0005DFC8`, VA `0x0045EBC8`:

```text
%u GS %u GS %f GS %f GS %u GS %f GS %u GS %f GS %f GS %f GS %u GS %u GS %u GS
```

Im Binary sind `GS` jeweils echte Bytes `0x1D`, ohne die hier zur Lesbarkeit eingefügten Leerzeichen. Die drei letzten Felder werden ebenso als ASCII-Zahlen gelesen. Nach dem letzten Feld steht im Format nochmals GS. Ein robuster Parser akzeptiert die 13 Felder mit oder ohne abschließendes GS. Vor dem Scan setzt VA `0x0045E986` den Dezimaltrenner auf `.`; die ursprüngliche Locale wird danach wiederhergestellt.

| Index | Inhalt | Numerischer Typ |
| ---: | --- | --- |
| 0 | Trainingszeit, Sekunden | uint |
| 1 | Herzfrequenz, Schläge/min | uint |
| 2 | Geschwindigkeit; Einheit siehe unten | float |
| 3 | Steigung, Prozent | float |
| 4 | Trainingsdistanz, Meter | uint |
| 5 | Kadenz, Umdrehungen/min | float |
| 6 | Leistung, Watt | uint |
| 7 | Physikalische Energie, kJ | float |
| 8 | Realistische Energie, kJ | float |
| 9 | Drehmoment, Nm | float |
| 10 | Gang plus 1 | uint |
| 11 | Gerätezustand | uint |
| 12 | Kadenzstatus plus 1 | uint |

Die Bedeutung der Felder und Firmwarevoraussetzung stammen aus dem Herstellerdokument [SP-KOMM-PM1, Version 1.0, Abschnitt 2.1.21](https://manualzz.com/doc/1228127/daum-electronic-bike--lyps--run-family-user-manual): X70 ab Firmware 1.380. Gangwert 1 bedeutet Schaltung aus, 2–29 bedeutet Gang 1–28. Gerätezustand 0/1 bedeutet aus/an; Kadenzstatus 1/2/3 bedeutet passend/zu niedrig/zu hoch. Pulsvalidität kann separat mit P00 abgefragt werden. Puls 0 als fehlender Messwert ist eine Loggerkonvention.

### Zwei Widersprüche im Herstellerdokument

1. **Geschwindigkeit:** Das veröffentlichte Dokument nennt für X70 km/h. Der lokale Parser multipliziert Feld 2 jedoch an VA `0x0045EAC0` mit **3.6**, bevor er es ins Ergebnis schreibt. Die Extended-Float-Konstante an `0x0045EBF0` ist `66 66 66 66 66 66 66 E6 00 40`, also 3.6. Der S02-Setter teilt seine Eingangsgeschwindigkeit durch dieselbe Konstante (`0x004604D7`), passend zum dokumentierten S02-Wert in m/s. Unabhängig davon rechnet die aktive [Incyclist-Premium-Implementierung](https://github.com/incyclist/devices/blob/ce018f7b269afbab8dc06a929461ed11b469da44/src/serial/daum/premium/utils.ts) bei X70 ebenfalls Rohwert mal 3.6; ihr [Kommunikationstest](https://github.com/incyclist/devices/blob/ce018f7b269afbab8dc06a929461ed11b469da44/src/serial/daum/premium/comms.test.ts) erwartet aus Rohwert 10 die Geschwindigkeit 36. **Der Logger interpretiert X70 standardmäßig als m/s.** Eine konfigurierbare km/h-Option deckt den Dokumentkonflikt ab. Nach der ersten echten Verbindung ist der Wert mit der Cockpitanzeige zu vergleichen.
2. **Statusfelder:** Das Dokument schreibt für die letzten drei Felder `%c`, die Herstelleranwendung und Incyclist parsen aber `%u`/`parseInt`. Incyclists Testfixture verwendet Kadenzstatus 0 für normal; die dokumentierte Verschiebung um 1 ist daher ebenfalls nicht für jede Variante gesichert. Die untersuchte Herstelleranwendung prüft keine Statusbereiche. Der Logger akzeptiert ASCII-numerische Statusfelder und die dokumentierte Rohbytevariante. Gang 2–29 wird als aktiver nativer Gang ausgewertet; Gerätezustand und Kadenzstatus werden validiert, aber nicht als Trainingsmesswerte verwendet. Im Transport können unescaped Rohsteuerbytes mit der ETB-Framegrenze kollidieren; der analysierte Herstellerparser endet am ersten ETB. Die Rohbyteunterstützung des Parsers ist daher kein Nachweis, dass jedes solche Paket über den realen Transport verarbeitet werden kann. Die proprietären Escape-Funktionen der untersuchten DLL betreffen andere, binäre Kommandos und werden dort für X70 nicht aufgerufen.

## Synthetischer Testvektor

Dieser Vektor ist selbst konstruiert, kein Mitschnitt eines Geräts:

```text
\x01X7060\x1d120\x1d7.00\x1d0.0\x1d420\x1d80.0\x1d150\x1d9.0\x1d36.0\x1d12.0\x1d2\x1d1\x1d1\x1d45\x17
```

Er codiert 60 s, 120 bpm, standardmäßig 7 m/s = 25.2 km/h, 420 m, 80 rpm und 150 W. Die Prüfsumme des Headers und aller Nutzdaten einschließlich abschließendem GS ist 45. Für die Anfrage `V00 ` mit zusätzlichem Leerzeichen ergibt sich dagegen eine andere Prüfsumme und ein unnötiges Datenfeld; reine Abfragen werden ohne dieses Leerzeichen erzeugt.

## Grenzen und Anwendung

Die binäre Analyse belegt das Verhalten des untersuchten Herstellertreibers. Die verlinkten Beschreibungen und Incyclist dienen als weitere Protokollreferenzen, ersetzen aber keinen Gerätetest. Firmwareabweichungen, mehrere gleichzeitig verbundene Clients und aktive Trainings-/Pausenzustände bleiben am Gerät zu prüfen. Herstellerprogramme und -bibliotheken werden weder in den Container noch in die veröffentlichten Pakete aufgenommen.

Der Logger liest Version und X70, bestätigt Frames und schreibt Trainingsdateien. Sollwert-, Tastatur-, Start/Stop-, Programmtransfer- und Benutzerkommandos gehören nicht zum beobachtenden Aufzeichnungsweg. Die üblichen Schreibbefehle S23/U10 und proprietäres M70 werden dafür nicht benötigt.
