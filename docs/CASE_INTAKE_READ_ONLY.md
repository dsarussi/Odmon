# Read-only case intake — first vertical slice

This slice discovers only the three confirmed source document names from
`dbo.vwExportToOuterSystems_Documents` for one `TikCounter`:

- `טופס_דיווח_דיגיטלי_1` — primary `NotificationForm` source; its PDF text layer is parsed.
- `מכתב_דרישה_בשם_לחברה_1` — fallback `DemandForm` source; its PDF text layer is parsed.
- `מכתב_שיבוב_לגורם_פרטי_1` — fallback `DemandForm` source; its PDF text layer is parsed.

The two demand forms have equal source strength and are always parsed independently.
The merged result selects a valid notification value first, falls back to demand only
when the notification value is missing or invalid, groups equal normalized candidates,
and preserves every valid conflicting value. Equal values from two or more documents
set `CrossValidationSucceeded=true`; disagreement sets `HasConflict=true` and never
creates a winner between demand documents.

Demand parsing also returns `AppraiserFeeAmount` and `LossOfValueAmount`. Vehicle
damage, total demand/paid, unclassified total, and deductible-related values remain
explicit `FinancialCandidates`; they are not mapped to Odcanit fields.

## Safe one-case exercise

From a secured terminal with the existing Odcanit read connection configured, run:

```powershell
dotnet run --project Odmon.Worker.csproj -- --case-intake-tik-counter 40514
```

Replace `40514` with another specific positive `TikCounter` when needed. This one-shot mode resolves
only the scoped read service, prints an indented structured JSON result, and exits
before the production host builder is created. Its isolated service container has no
`IHostedService`, Integration DB, Monday client, or Odcanit writer registrations. The
JSON can contain personal data extracted from the source
document, so its console/output should be handled accordingly.

The flow performs a parameterized `SELECT`, reads only the returned PDF UNC path,
validates the `%PDF` signature, and extracts the existing text layer. It performs no
Odcanit, Monday, or Integration DB writes and has no OCR/LLM fallback.
