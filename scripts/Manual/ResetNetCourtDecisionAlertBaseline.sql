/*
Manual cleanup for the previous NetCourt historical-baseline behavior.

Run against IntegrationDb only, with NetCourtDecisionAlertWorker stopped.
This script:
  1. Deletes only NetCourtDecisionAlerts rows whose Status is 'Baseline'.
  2. Deletes the singleton state row so the next worker run initializes
     LastSeenCounter from the current MAX(Counter) without scanning history.

Review the preview counts before executing.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

SELECT COUNT_BIG(*) AS BaselineRowsToDelete
FROM dbo.NetCourtDecisionAlerts
WHERE Status = N'Baseline';

SELECT *
FROM dbo.NetCourtDecisionAlertState
WHERE Id = 1;

BEGIN TRANSACTION;

DELETE FROM dbo.NetCourtDecisionAlerts
WHERE Status = N'Baseline';

DECLARE @DeletedBaselineRows bigint = @@ROWCOUNT;

DELETE FROM dbo.NetCourtDecisionAlertState
WHERE Id = 1;

DECLARE @DeletedStateRows int = @@ROWCOUNT;

COMMIT TRANSACTION;

SELECT
    @DeletedBaselineRows AS DeletedBaselineRows,
    @DeletedStateRows AS DeletedStateRows;
