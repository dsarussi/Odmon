# EmailFiling delta processing investigation

## Finding and evidence

The reported production counts were 1,712 resolution runs for 53 message fingerprints in 24 hours. The filing state had a populated cursor, but both `LastSuccessfulSyncUtc` and `UpdatedAtUtc` remained at 2026-09-02 15:59:35.2680322 UTC while processing continued the following day. This is consistent with repeated incomplete cycles, rather than completed incremental rounds. Failure categories and the deployed binary have not been independently inspected, so the specific production failure that prevented progress remains unconfirmed.

The reproducible amplification mechanism is:

1. Polling starts from the persisted filing cursor.
2. Each message enters resolution and saves its diagnostic and ResolutionRun before the mailbox cursor is saved.
3. A later message or Graph page fails. Polling exits without advancing the cursor.
4. The next poll or worker restart retrieves the same batch. Previously completed messages enter resolution again because there was no per-message processing completion check.
5. `EmailFilingDedups` correctly prevents duplicate writes, but that check occurs after resolution.

Before the fix, the new failed-traversal/restart regression and unchanged-redelivery regression each produced two ResolutionRuns where one was expected. The six initial cursor persistence, empty-cycle, pagination, failure, and state-isolation cases already passed.

Graph can also repeat a change in subsequent responses. Message delta can deliver read/unread events despite the change filter. The existing `changeType=created` request is therefore insufficient as a processing idempotency mechanism. Sources: [Graph delta replays](https://learn.microsoft.com/en-us/graph/delta-query-overview#replays) and [message delta filtering](https://learn.microsoft.com/en-us/graph/api/message-delta?view=graph-rest-1.0).

## State audit

- Filing reads and writes `EmailFilingMailboxStates`, keyed by trimmed, lowercase mailbox and trimmed folder (default `Inbox`). Forwarding independently uses `EmailAutomationMailboxStates`.
- The worker creates a new dependency-injection scope for each cycle. EF tracking is enabled; no filing path clears tracking or resets the persisted cursor.
- The Graph client validates and follows returned opaque cursor URLs. It builds the initial filtered URL only when no cursor exists. Neither query reconstruction nor cursor truncation was found.
- Empty pages with a `nextLink` are followed. The final `deltaLink` and successful-sync timestamp are saved only after all messages in the completed traversal succeed. Missing final links and traversal failures leave the completed state unchanged.
- Previously, the cycle limit saved a resumable `nextLink` in the `DeltaLink` column. This could represent an incomplete round but was not itself a replay bug: the next poll read and followed that URL. The fix stores it separately so completed and in-progress state are distinct.

## Implemented behavior

Polling now compares the latest successfully processed content fingerprint for the mailbox and existing message fingerprint before invoking `EmailFilingService.ProcessAsync`. An unchanged delivery skips the entire resolver/diagnostic/write pipeline. A changed fingerprint enters that pipeline unchanged, including its existing write dedup.

The content fingerprint is an HMAC-SHA256 using the existing fingerprint key over the fields already supplied to filing: subject, sender, body, body content type, To/Cc/Bcc recipients, and received/sent times. Recipient ordering is ignored. Raw content is used only in memory; only the keyed digest is persisted. Graph delivery IDs, read flags, change keys, and modification timestamps are excluded. Existing message identity and write-dedup identity are unchanged.

`ProcessedContentFingerprint` is saved on the returned diagnostic only after processing returns successfully. It is saved per message, so a later failure does not invalidate earlier completions. Failed messages have no completion fingerprint and remain retryable. The latest successful version is compared, allowing a change back to earlier content to be processed again.

`NextLink` stores the existing page-boundary cycle-limit checkpoint. `DeltaLink` continues to hold the completed-round cursor; completion replaces it, clears `NextLink`, and advances `LastSuccessfulSyncUtc`. Legacy page cursors already stored in `DeltaLink` are accepted and followed normally. Skipped unchanged deliveries do not consume the processing limit. Existing page-boundary behavior is retained: a returned page is fully processed before checkpointing.

Only aggregate counts (`MessagesProcessed`, `UnchangedSkipped`, `Pages`) were added to cycle logs. No email content or identifiers were added to logs.

## Changed files

- `Services/EmailFilingPollingService.cs`: processing completion check, content HMAC, separate page checkpoint, aggregate counts.
- `Services/EmailFilingService.cs`: existing message-fingerprint method made internal for reuse; method body and processing behavior unchanged.
- `Models/EmailFilingModels.cs`: two nullable properties.
- `Data/IntegrationDbContext.cs`: mappings for those properties.
- `Migrations/20260903140647_AddEmailFilingProcessingCheckpoint.cs`: two nullable columns, with corresponding Down operations.
- `Migrations/20260903140647_AddEmailFilingProcessingCheckpoint.Designer.cs`: generated migration metadata.
- `Migrations/IntegrationDbContextModelSnapshot.cs`: generated snapshot update.
- `Odmon.Worker.Tests/EmailFilingTests.cs`: test class made partial to reuse existing fixtures.
- `Odmon.Worker.Tests/EmailFilingDeltaPollingTests.cs`: 13 regression cases.
- `docs/EMAILFILING_DELTA_PROCESSING_FIX.md`: this investigation and handoff.

Resolver, Direct Insurance, authority gates, Odcanit writes, `EmailFilingDedups`, and forwarding implementation remain unchanged. Pre-existing hearing-backfill and system-documentation edits were not modified by this work.

## Validation

- New delta regression cases: **13 passed**, covering first/empty subsequent cycle, fresh-context restart, empty-page pagination, traversal/missing-link failures, normalized state key, forwarding-state isolation, redelivery, failed traversal across restart, cycle-limit checkpoint and restart, changed/reverted content, failed-message retry and retained write dedup, legacy diagnostics, and keyed content comparison.
- `dotnet test Odmon.Worker.Tests/Odmon.Worker.Tests.csproj --no-restore --filter "FullyQualifiedName~EmailFiling|FullyQualifiedName~EmailAutomation"`: **198 passed, 0 failed, 0 skipped**.
- `dotnet test Odmon.Worker.Tests/Odmon.Worker.Tests.csproj --no-build --no-restore`: **763 passed, 0 failed, 0 skipped**.
- `dotnet build Odmon.Worker.csproj --configuration Release --no-restore`: **succeeded, 0 warnings, 0 errors**.
- `git diff --check`: passed.

Tests use scripted Graph responses, fake external writers/resolvers, and fresh EF InMemory contexts sharing a database for restart checks. No live Graph or production database validation was performed. The migration was scaffolded with a temporary offline design-time factory, which was removed afterward.

## Remaining limits and release requirement

- The additive migration must be applied through the normal release process before running this binary. It was generated but not applied. No commit, push, deployment, or production migration was performed.
- Older diagnostics have no content digest. On redelivery, one successful processing pass establishes the baseline; existing write dedup still prevents a duplicate filing.
- An underlying failing message or Graph request can still prevent the round from finishing and cause mailbox refetches. Completed unchanged messages are skipped on retry, but resolving that failure requires its sanitized cycle error category. This fix does not discard failed messages or advance past them.
- A crash or database error between successful processing and the extra completion-marker save can cause another resolution attempt. Write dedup remains the final protection.
- The completion check is not a distributed lock. Concurrent worker instances can still resolve the same message before either saves completion. Write dedup remains unchanged.
- Removing completion diagnostics or rotating the existing fingerprint key can make a replay eligible again. Diagnostic retention must account for this use.
- Only content in the existing Graph projection is compared. Attachment-only changes, missing Graph fields, and Graph token expiry retain the limitations of the existing reader. No new update subscriptions, MIME reads, token resets, or attachment rules were introduced.

To identify the production blocker without exposing email data, inspect the existing worker's cycle failure categories and aggregate diagnostic `FinalDecision` counts over the affected interval. Mailbox addresses, cursor URLs, subjects, senders, recipients, claim numbers, and raw exception messages are unnecessary for that follow-up.
