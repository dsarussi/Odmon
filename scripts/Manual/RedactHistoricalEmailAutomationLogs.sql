/*
  Optional, operator-controlled cleanup for EmailAutomationLogs rows created
  before the privacy-minimizing audit policy was deployed.

  Review the affected row count and take the normal approved database backup
  before executing in production. This script does not delete audit rows.
*/

SET XACT_ABORT ON;
BEGIN TRANSACTION;

SELECT COUNT_BIG(*) AS RowsToRedact
FROM dbo.EmailAutomationLogs
WHERE InternetMessageId IS NOT NULL
   OR GraphMessageId NOT LIKE REPLICATE('[0-9A-F]', 64)
   OR Subject IS NOT NULL
   OR Sender IS NOT NULL
   OR ReceivedDateTimeUtc IS NOT NULL
   OR DetectedCourtCaseNumber IS NOT NULL
   OR ResolvedTikNumber IS NOT NULL
   OR ResolvedClientNumber IS NOT NULL
   OR ActualForwardTo IS NOT NULL
   OR TargetEmail IS NOT NULL
   OR RuleName IS NOT NULL
   OR IdempotencyKey IS NOT NULL
   OR ErrorMessage IS NOT NULL;

UPDATE dbo.EmailAutomationLogs
SET RuleName = NULL,
    InternetMessageId = NULL,
    GraphMessageId = CONCAT('REDACTED-', Id),
    Subject = NULL,
    Sender = NULL,
    ReceivedDateTimeUtc = NULL,
    DetectedCourtCaseNumber = NULL,
    ResolvedTikNumber = NULL,
    ResolvedClientNumber = NULL,
    ActualForwardTo = NULL,
    TargetEmail = NULL,
    IdempotencyKey = NULL,
    ErrorMessage = CASE
        WHEN ErrorMessage IS NULL THEN NULL
        ELSE 'HistoricalErrorRedacted'
    END;

-- Inspect the transaction before replacing ROLLBACK with COMMIT.
SELECT TOP (100)
    Id,
    Mailbox,
    GraphMessageId,
    ResolvedTikCounter,
    ResolvedTargetEmail,
    Action,
    ErrorMessage,
    CreatedAtUtc,
    ProcessedAtUtc
FROM dbo.EmailAutomationLogs
ORDER BY Id DESC;

ROLLBACK TRANSACTION;
-- COMMIT TRANSACTION;
