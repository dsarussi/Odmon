# NetCourt Decision Alerts

## Overview

`NetCourtDecisionAlertWorker` is independent from Monday synchronization. It reads Odcanit decisions, resolves the related case and client, records durable deduplication state in the Integration DB, and queues one Hebrew email per untracked decision.

## Detection

- Source: `vwNetCourtDocs`
- Filter: `DocType IN (2, 3)`
- Business date filter: `DocDate >= NetCourtDecisionAlerts:StartFromDocDate`
- `tsCreateDate`, filename, description, and decision text do not classify decisions.
- `Counter` may be used for deterministic ordering or tracking, but it is not the polling watermark.
- Durable `DocumentIdentity` tracking prevents repeat emails across worker restarts.

## Email Modes And Routing

`EmailMode=Test` reads real eligible rows and resolves the intended employee, but sends the primary email only to `TestRecipient`. `EmailMode=Live` sends the primary email to the configured employee.

| Client numbers | Live recipient | Display name |
|---|---|---|
| `2`, `15` | `yonatan@ezer-law.com` | `יונתן` |
| `5`, `8`, `3`, `23`, `253` | `amir@ezer-law.com` | `אמיר` |

`BccRecipients` applies only to NetCourt decision emails. In Live mode, `To` remains the routed employee and the configured monitoring recipients are BCC. In Test mode, `To` remains `TestRecipient` and configured monitoring recipients are BCC. Empty or missing BCC configuration has no effect.

An unmapped client is recorded as `MissingRouting`. With `FallbackRecipientEnabled=false`, no primary or BCC-only email is sent.

## PDF Attachment

The PDF path is resolved from Odcanit by calling:

```sql
EXEC dbo.procDocumentsGroup_BuildDocPath
    @DocCounter = ODDocID,
    @DocExtension = N'.pdf',
    @ProtectedDocPath = @Path OUTPUT;
```

ODMON does not construct case paths, search by filename, or enumerate document folders. The procedure result must:

- Be under a configured `AttachmentAllowedRoots` entry.
- Exist and be readable.
- Have a `.pdf` extension.
- Be no larger than `MaxAttachmentBytes` (25 MB by default).
- Start with the PDF signature `%PDF`.

Attachment handling is best effort. Missing, inaccessible, oversized, or invalid files do not block the alert and do not change deduplication. The email is sent without the attachment and includes:

```text
לא צורף קובץ ההחלטה: [סיבה]
```

At most one PDF is attached. SMTP retries and rate limiting remain owned by `EmailNotifier`.

## Failure Reporting

Unexpected worker failures, SQL failures, and other operational exceptions use the existing `QueueCriticalAlert` path. Those messages use global `Email:Recipients` and never inherit NetCourt BCC recipients. Daily summaries and other ODMON email types are also unchanged.

## Production Configuration

```json
"NetCourtDecisionAlerts": {
  "Enabled": true,
  "IntervalSeconds": 300,
  "StartFromDocDate": "2026-06-07",
  "MaxBatchSize": 100,
  "AttachDecisionPdf": true,
  "MaxAttachmentBytes": 26214400,
  "AttachmentAllowedRoots": [
    "\\\\dc22\\Odlight\\Docs\\",
    "D:\\Odlight\\Docs\\"
  ],
  "EmailMode": "Live",
  "TestRecipient": "odmon@ezer-law.com",
  "BccRecipients": [
    "odmon@ezer-law.com"
  ],
  "FallbackRecipientEnabled": false,
  "ClientNumberToRecipientEmail": {
    "2": "yonatan@ezer-law.com",
    "15": "yonatan@ezer-law.com",
    "5": "amir@ezer-law.com",
    "8": "amir@ezer-law.com",
    "3": "amir@ezer-law.com",
    "23": "amir@ezer-law.com",
    "253": "amir@ezer-law.com"
  }
}
```

## Safe Test Procedure

1. Set `Enabled=true`, `EmailMode=Test`, and `TestRecipient=odmon@ezer-law.com`.
2. Keep production routing configured so intended recipients and Hebrew display names are exercised.
3. Confirm all primary messages arrive only at the test recipient; BCC monitoring may also contain the same mailbox.
4. Confirm logs report attachment present or absent, without requiring an attachment for success.
5. Verify an unmapped client creates `MissingRouting` and queues no email.
6. Review Integration DB deduplication rows and rerun the worker to confirm no duplicate message is queued.
7. Switch to `EmailMode=Live` only after these checks pass.
