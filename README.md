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

## Kjøre lokalt

```bash
dotnet run --framework net8.0
```

Løsningen kan også bygges for både .NET 8 og .NET 10:

```bash
dotnet build
```

Åpne adressen som vises i terminalen. Under lokal utvikling brukes testfilene
som er definert i `appsettings.Development.json`.
