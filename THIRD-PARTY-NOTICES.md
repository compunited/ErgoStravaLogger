# Third-party components and references

## Runtime and build dependencies

The application and its test executable use .NET 10 / ASP.NET Core framework components. The project files declare no third-party NuGet package references. Framework components and container base images retain their own licenses and notices; the license selected for this project's source does not replace them.

- [.NET runtime license and notices](https://github.com/dotnet/runtime)
- [ASP.NET Core license and notices](https://github.com/dotnet/aspnetcore)
- [Microsoft .NET container image licensing information](https://github.com/dotnet/dotnet-docker/blob/main/LICENSE)

## Interoperability references

The source and protocol documentation cite the following references for message formats, identifiers and interoperability behavior:

- [WFTNP / Direct Connect protocol description](https://github.com/elfrances/wahoo-fitness-tnp) (upstream document published under GPL-3.0; not reproduced here)
- [Bluetooth Fitness Machine Service](https://www.bluetooth.com/specifications/specs/fitness-machine-service-1-0-1/) and the Cycling Power / Heart Rate service specifications
- [RFC 6762: Multicast DNS](https://datatracker.ietf.org/doc/html/rfc6762) and [RFC 6763: DNS-Based Service Discovery](https://datatracker.ietf.org/doc/html/rfc6763)
- [Strava API documentation](https://developers.strava.com/docs/)
- [Garmin Training Center XML schemas](https://www8.garmin.com/xmlschemas/TrainingCenterDatabasev2.xsd)
- [Incyclist's Daum Premium implementation](https://github.com/incyclist/devices/tree/ce018f7b269afbab8dc06a929461ed11b469da44/src/serial/daum/premium), cited for comparison of protocol behavior

The repository documents its own implementation and a static analysis of a locally installed manufacturer application. Manufacturer binaries are excluded from this distribution. Referenced implementations and documents retain their respective rights; these links do not grant permission to redistribute their source or binaries. The source describes WFTNP framing as independently implemented without copying library code.

## Names and compatibility

Daum, Strava, Zwift, Wahoo, Garmin and Bluetooth names are used to identify devices, services, formats and interoperability targets. Their respective owners retain rights in those names and marks. This is an independent project; no vendor endorsement, certification or affiliation is claimed.
