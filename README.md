# NextToVb-loggviewer

En ASP.NET Core-basert loggviser for NextToVB-miljøet. Løsningen leser IIS-
logger, inbound-logger og outbound-logger direkte fra konfigurerte mapper og
viser både rådata og oppsummerte måltall i nettleseren.

## Funksjoner

- Finner og viser tilgjengelige IIS-loggfiler.
- Leser inbound- og outbound-logger per dato.
- Parser IIS-format med felter som tidspunkt, metode, URI, statuskode,
	datamengde og behandlingstid.
- Parser applikasjonslogger med nivå, tidspunkt og flerliniemeldinger.
- Kobler request- og response-filer ved hjelp av felles ID og tidspunkt.
- Viser oppsummeringer, filtrering og metadata i separate faner.
- Begrenses til mapper som er angitt i `LogViewer:AllowedRoots`.
- Har diagnostikk-endepunktet `/api/diagnostics/paths` for kontroll av
	konfigurerte stier og tilgang.

## Konfigurasjon

Produksjonsstier legges i `appsettings.json`. Lokale teststier kan legges i
`appsettings.Development.json`, som brukes når miljøvariabelen
`ASPNETCORE_ENVIRONMENT` er satt til `Development`.

Følgende innstillinger brukes:

- `LogViewer:AllowedRoots`: Mappene løsningen får lov til å lese fra.
- `IIS:DefaultLogPath`: Standard IIS-logg eller loggmappe.
- `InboundLogPath`: Mappe for inbound-logger.
- `OutboundLogPath`: Mappe for outbound-logger.
- `IIS TestFile`, `Inbound TestFile` og `Outbound TestFile`: Valgfrie
	testfiler som overstyrer standardstiene.
- `LocksApi:BaseUrl`: HTTPS-adressen til DNBIntegrationServices.
- `LocksApi:Username` og `LocksApi:Password`: Basic Auth for lock-endepunktene.
- `LocksDb:ConnectionString`: Valgfri tilkobling til konfigurasjonsdatabasen
	(`NextToVB`) der tabellen `LockStore` ligger. Når den er satt, leses låsene
	direkte fra tabellen. Da vises også Assignment-låser, som
	`api/Assignments/Locks` ikke klarer å returnere (HTTP 500), og tidspunktet
	låsen ble opprettet. Unlock går fortsatt via API-et. Eksempel:
	`Data Source=.;Initial Catalog=NextToVB;Integrated Security=True;TrustServerCertificate=True`

Låser av typen Assignment vises i Låser-fanen, men kan ikke låses opp fordi
API-et ikke har et aktivt unlock-endepunkt for denne typen. Password bør settes
via miljøvariabel eller annen hemmelig konfigurasjon i produksjon.

Alle filstier valideres mot `AllowedRoots` før filer åpnes. Dette hindrer at
nettleseren kan be serveren lese vilkårlige filer utenfor de godkjente
mappene.

## Tilgang til IIS-mapper

IIS-applikasjonspoolen må ha lese- og kjørerettighet til loggmappene. Dette
ble satt på Windows-mappen med:

```powershell
icacls "C:\inetpub\logs\LogFiles\W3SVC2" /grant "IIS AppPool\LoggViewer:(OI)(CI)(RX)" /T
```

Betydningen av parameterne er:

- `RX`: Read/Execute, altså lese- og traverseringstilgang.
- `(OI)`: Rettigheten arves av filer i mappen.
- `(CI)`: Rettigheten arves av undermapper.
- `/T`: Endringen gjentas rekursivt for eksisterende innhold.

Samme prinsipp må brukes på hver loggmappe som skal leses. Kommandoen gir
ikke skrivetilgang til loggene.

For å sette samme tilgang rekursivt og samtidig sikre at nye IIS-loggfiler
arver rettighetene, kan [Set-IisLogReadAccess.ps1](Set-IisLogReadAccess.ps1)
kjøres som administrator på serveren:

```powershell
.\Set-IisLogReadAccess.ps1 -LogPath 'C:\inetpub\logs\LogFiles' -AppPoolName 'LoggViewer'
```

Scriptet gir apppool-identiteten `RX`-tilgang, som betyr lese- og
traverseringstilgang, men ikke skrivetilgang. `OI` og `CI` sørger for arv til
filer og undermapper som opprettes senere. Test endringen først uten å gjøre
den med `-WhatIf`.

NTFS-rettigheter kan ikke styre om en prosess holder en fil åpen. LoggViewer
bruker derfor `FileShare.ReadWrite | FileShare.Delete` ved lesing, slik at IIS
kan skrive til og rotere loggfiler mens de leses.

## Lesing av aktive og skrive-låste logger

Loggfiler kan være åpne og bli skrevet til av IIS eller applikasjonen mens
LoggViewer leser dem. Derfor åpner parserne filene med:

```csharp
FileAccess.Read,
FileShare.ReadWrite | FileShare.Delete
```

Dette betyr at LoggViewer selv bare ber om lesetilgang, samtidig som andre
prosesser får fortsette å skrive til eller rotere filen. Mappetillatelsen og
`FileShare`-innstillingen løser to forskjellige problemer: mappetillatelsen
avgjør om IIS-applikasjonspoolen får tilgang til filen, mens `FileShare`
avgjør om filen kan åpnes når den allerede er i bruk.

## Bygging og kjøring

`Debug` brukes bare ved lokal utvikling. `Release` brukes alltid når
applikasjonen skal publiseres til Windows/IIS.

Bygg for lokal kontroll:

```bash
dotnet build -c Debug -f net8.0
```

Kjør lokalt:

```bash
dotnet run -c Debug --framework net8.0
```

Prosjektet bygges mot både .NET 8 og .NET 10, og begge publiseres ved hver
deploy fordi serverne kjører begge versjonene. En vanlig `dotnet build` uten
`-c` er ikke en deploy-build.

## Deploy på Windows/IIS

Applikasjonen skal kjøres på Windows-serveren, ikke direkte i macOS-miljøet.
Publiser fra utviklingsmaskinen med `Release`-konfigurasjon og Windows x64 som
mål. Dette lager ett deployartefakt per .NET-versjon under `artifacts/publish`:

```bash
dotnet publish -c Release -f net8.0 -r win-x64 --self-contained false -o ./artifacts/publish/net8.0/win-x64
dotnet publish -c Release -f net10.0 -r win-x64 --self-contained false -o ./artifacts/publish/net10.0/win-x64
```

Kopier innholdet i mappen som passer serverens .NET-versjon
(`artifacts/publish/net8.0/win-x64` eller `artifacts/publish/net10.0/win-x64`)
til serveren, sammen med
`Set-IisLogReadAccess.ps1`. Ikke kopier `bin/Debug`, `bin/Release` eller
`obj`; disse er lokale bygge- og mellomfiler og skal ikke deployes. Installer
riktig .NET 8 eller .NET 10 Hosting Bundle på serveren dersom den ikke allerede er
installert. Kjør deretter PowerShell som administrator på Windows-serveren:

```powershell
Set-Location 'C:\Apps\LoggViewer'
& .\Set-IisLogReadAccess.ps1 `
	-LogPath 'C:\inetpub\logs\LogFiles' `
	-AppPoolName 'LoggViewer'
```

Før første kjøring kan tilgangene kontrolleres uten endringer:

```powershell
& .\Set-IisLogReadAccess.ps1 `
	-LogPath 'C:\inetpub\logs\LogFiles' `
	-AppPoolName 'LoggViewer' `
	-WhatIf
```

Sett produksjonsverdier som miljøvariabler for IIS-applikasjonen, for eksempel:

```powershell
[Environment]::SetEnvironmentVariable('LocksApi__BaseUrl', 'https://integration.example.no', 'Machine')
[Environment]::SetEnvironmentVariable('LocksApi__Username', 'amesto', 'Machine')
[Environment]::SetEnvironmentVariable('LocksApi__Password', '<hemmelig-passord>', 'Machine')
```

Start apppoolet på nytt etter endringer i miljøvariabler. `LocksApi:BaseUrl`
må være HTTPS, med unntak av `http://localhost`, som støttes for lokal API-
installasjon. Unngå å legge produksjonspassord i `appsettings.json` eller
committe det i repositoryet.

### Sjekk før deploy

Kontroller at publiseringen faktisk er `Release` og Windows x64:

```bash
dotnet publish -c Release -f net8.0 -r win-x64 --self-contained false -o ./artifacts/publish/net8.0/win-x64
dotnet publish -c Release -f net10.0 -r win-x64 --self-contained false -o ./artifacts/publish/net10.0/win-x64
```

Det som skal kopieres til IIS-serveren er kun innholdet i
`artifacts/publish/net8.0/win-x64` eller `artifacts/publish/net10.0/win-x64`,
avhengig av serverens .NET-versjon. Under lokal utvikling brukes testfilene som er
definert i `appsettings.Development.json`; på serveren brukes
`appsettings.json` og IIS-miljøvariabler.
