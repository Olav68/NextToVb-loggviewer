# Locks API – dokumentasjon for implementasjon

Kilde: `DNBIntegrationServices` (klassisk ASP.NET Web API, prosjektet `DNBIntegration`).
Alle ruter under er verifisert direkte mot kildekoden (`Controllers/AssignmentsController.cs`, `OutboundInvoiceController.cs`, `PostingsController.cs`, `RemitsController.cs`, `BaseController.cs`, `Filters/IdentityBasicAuthenticationAttribute.cs`, `Common/SystemState.cs`, `DNBIntegrationServices.Model/SystemLock.cs`).

## 1. Hva er en "lock"?

Når integrasjonen mottar en hendelse fra Vitec Hub/Next (oppdrag, postering, remittering, utgående faktura) og noe går galt eller krever manuell oppfølging, blir posten låst (`SystemLock`) i stedet for å feile stille. En lås markerer at en gitt post for en gitt installasjon står fast til noen aktivt fjerner den (`Unlock`). Det finnes fire låstyper:

| Låstype (`LockType`) | Gjelder | Kontroller |
|---|---|---|
| `Assignment` | Oppdrag/estate-varsler | `AssignmentsController` |
| `Posting` | Posteringer | `PostingsController` |
| `Remit` | Remitteringer | `RemitsController` |
| `OutboundInvoice` | Utgående fakturaer | `OutboundInvoiceController` |

## 2. Autentisering

Alle endepunktene under er beskyttet av to lag, begge må være oppfylt:

1. **HTTP Basic Authentication** (`IdentityBasicAuthenticationAttribute`, satt globalt på `BaseController`). Brukernavn/passord valideres mot `APIUsers` i installasjonskonfigurasjonen (ikke Windows-/AD-brukere). Header:
   ```
   Authorization: Basic base64(brukernavn:passord)
   ```
2. **`[Authorize(Users = "amesto")]`** på hvert enkelt lock-endepunkt. Dette betyr at Basic Auth-brukernavnet i praksis må være nøyaktig `amesto` – andre gyldige API-brukere slipper ikke gjennom disse endepunktene selv om de er autentisert.

I tillegg krever `BaseController` HTTPS (`[RequireHttps]`) – kall over http avvises.

Manglende/feil credentials gir `401 Unauthorized`.

Eksempel (fra `DNBIntegrationAdminWinService`, som er den eneste kjente konsumenten i dag):

```csharp
var byteArray = Encoding.ASCII.GetBytes($"{userName}:{password}");
client.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
```

## 3. Datamodell

```csharp
public enum LockType
{
    Assignment,
    Posting,
    Remit,
    OutboundInvoice
}

public class SystemLock
{
    public string InstallationId { get; set; }
    public string LockId { get; set; }
    public LockType TypeOfLock { get; set; }
    public string ErrorMessage { get; set; }
}
```

- `InstallationId`: installasjons-ID slik den er konfigurert for kunden (samme verdi som brukes i andre installasjonsspesifikke ruter, f.eks. `{installationId}` i posterings-/remitterings-endepunktene).
- `LockId`: identifiserer den låste posten (postering-/remit-/faktura-/oppdrags-ID) – brukes til å låse den opp igjen.
- `TypeOfLock`: serialiseres som streng-enum-navn (`"Posting"`, `"Remit"`, `"OutboundInvoice"`, `"Assignment"`) i JSON, ikke som tall, med mindre custom JSON-konfig er satt globalt (anbefales å verifisere mot faktisk respons ved første test).
- `ErrorMessage`: årsaken til at posten ble låst (kan være tom).

## 4. Endepunkter

**Viktig avvik å merke seg:** de fire låstypene er *ikke* symmetriske i hvilke endepunkter som finnes. `Assignment` har **ingen** aktivt Unlock-endepunkt i dagens kode (funksjonen finnes kommentert ut i `SystemState.cs`, men er ikke koblet til noen rute) – lås på oppdrag må fjernes på annen måte (f.eks. direkte mot databasen/LockRepo) inntil videre.

### 4.1 Assignments (oppdrag)

| Metode | Rute | Beskrivelse |
|---|---|---|
| GET | `/api/Assignments/Locks` | Alle oppdragslåser, for **alle aktive installasjoner** |

Ingen per-installasjon-GET og ingen Unlock-endepunkt finnes for denne typen.

Respons (200 OK):
```json
{
  "INSTALLASJON_A": [
    { "installationId": "INSTALLASJON_A", "lockId": "12345", "typeOfLock": "Assignment", "errorMessage": "..." }
  ],
  "INSTALLASJON_B": []
}
```

### 4.2 Postings (posteringer)

| Metode | Rute | Beskrivelse |
|---|---|---|
| GET | `/api/{installationId}/Postings/Locks` | Posteringslåser for **én** installasjon. `404 Not Found` hvis `installationId` ikke finnes blant aktive installasjoner. |
| GET | `/api/Postings/Locks` | Posteringslåser for **alle aktive** installasjoner (samme responsform som Assignments over). |
| POST | `/api/{installationId}/Postings/Locks/{lockId}/Unlock` | Fjerner en posteringslås. |

GET (per installasjon) – respons `200 OK`:
```json
[
  { "installationId": "INSTALLASJON_A", "lockId": "98765", "typeOfLock": "Posting", "errorMessage": "..." }
]
```

Unlock – ingen request-body kreves (tomt objekt er nok):
```http
POST /api/INSTALLASJON_A/Postings/Locks/98765/Unlock
Authorization: Basic ...
Content-Type: application/json

{}
```
Respons: `200 OK` ved suksess, `500 Internal Server Error` ved uventet feil (loggført server-side).

### 4.3 Remits (remittering)

| Metode | Rute | Beskrivelse |
|---|---|---|
| GET | `/api/{installationId}/Remits/Locks` | Remitteringslåser for én installasjon. `404` hvis installasjonen ikke finnes. |
| GET | `/api/Remits/Locks` | Remitteringslåser for alle aktive installasjoner. |
| POST | `/api/{installationId}/Remits/Locks/{lockId}/Unlock` | Fjerner en remitteringslås. |

Samme respons-/requestform som Postings (bytt `"typeOfLock": "Remit"`).

### 4.4 OutboundInvoices (utgående faktura)

| Metode | Rute | Beskrivelse |
|---|---|---|
| GET | `/api/OutboundInvoices/Locks` | Fakturalåser for **alle aktive** installasjoner. Ingen per-installasjon-GET finnes. |
| POST | `/api/{installationId}/OutboundInvoices/Locks/{lockId}/Unlock` | Fjerner en fakturalås. |

Samme respons-/requestform som de andre (bytt `"typeOfLock": "OutboundInvoice"`).

## 5. Oppsummeringstabell (for rask referanse)

| Låstype | List – alle installasjoner | List – én installasjon | Unlock |
|---|---|---|---|
| Assignment | `GET /api/Assignments/Locks` | – (finnes ikke) | – (finnes ikke) |
| Posting | `GET /api/Postings/Locks` | `GET /api/{installationId}/Postings/Locks` | `POST /api/{installationId}/Postings/Locks/{lockId}/Unlock` |
| Remit | `GET /api/Remits/Locks` | `GET /api/{installationId}/Remits/Locks` | `POST /api/{installationId}/Remits/Locks/{lockId}/Unlock` |
| OutboundInvoice | `GET /api/OutboundInvoices/Locks` | – (finnes ikke) | `POST /api/{installationId}/OutboundInvoices/Locks/{lockId}/Unlock` |

## 6. Implementasjonsnotater for ny app

- Bruk Basic Auth med brukernavnet `amesto` (eller det som faktisk er konfigurert i målmiljøets `APIUsers` – verifiser mot miljøkonfigurasjon, ikke anta at "amesto" er tilgjengelig i alle miljøer).
- Alle kall må gå over HTTPS.
- `Unlock`-kallene er idempotente i praksis (feiler ikke eksplisitt hvis låsen allerede er borte, basert på implementasjonen i `SystemState.Unlock`), men dette bør verifiseres i test før det stoles på i produksjon.
- Lås-tilstanden holdes i minnet bak en `lock`-blokk per låstype (`_assignmentsLock`, `_postingsLock`, `_remitsLock`, `_outboundInvoicesLock`) og persisteres via `DNBIntegration.LockRepo` – dvs. den er trådsikker på tjenersiden, men det er ingen bulk-unlock; hvert kall håndterer én lås.
- Vurder å polle listeendepunktene periodisk (f.eks. hvert 5.–15. minutt) fremfor push, siden det ikke finnes noen webhook/varsling ut når en lås oppstår.
- Siden `Assignment`-typen mangler Unlock-endepunkt, må den nye appen enten (a) ikke tilby unlock for denne typen ennå, eller (b) avklare med teamet om et nytt endepunkt skal bygges i `DNBIntegrationServices` først.

## 7. Kildereferanser

- `DNBIntegrationServices/Controllers/AssignmentsController.cs`
- `DNBIntegrationServices/Controllers/PostingsController.cs`
- `DNBIntegrationServices/Controllers/RemitsController.cs`
- `DNBIntegrationServices/Controllers/OutboundInvoiceController.cs`
- `DNBIntegrationServices/Controllers/BaseController.cs`
- `DNBIntegrationServices/Filters/IdentityBasicAuthenticationAttribute.cs`
- `DNBIntegrationServices/Common/SystemState.cs`
- `DNBIntegrationServices.Model/SystemLock.cs`
- `DNBIntegrationAdminWinService/Data/APIRepo.cs` (eksempel på faktisk konsument av Unlock)
