/*
Manual cleanup for the previous NetCourt historical-baseline behavior.

Run against IntegrationDb only, with NetCourtDecisionAlertWorker stopped.
This script:
  1. Deletes only NetCourtDecisionAlerts rows whose Status is 'Baseline'.
  2. Leaves NetCourtDecisionAlertState unchanged because state is no longer
     used for NetCourt document detection.

Review the preview counts before executing.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

SELECT COUNT_BIG(*) AS BaselineRowsToDelete
FROM dbo.NetCourtDecisionAlerts
WHERE Status = N'Baseline';

BEGIN TRANSACTION;

DELETE FROM dbo.NetCourtDecisionAlerts
WHERE Status = N'Baseline';

DECLARE @DeletedBaselineRows bigint = @@ROWCOUNT;

COMMIT TRANSACTION;

SELECT @DeletedBaselineRows AS DeletedBaselineRows;
