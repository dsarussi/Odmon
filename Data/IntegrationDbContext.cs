using Microsoft.EntityFrameworkCore;
using Odmon.Worker.Models;

namespace Odmon.Worker.Data
{
    public class IntegrationDbContext : DbContext
    {
        public IntegrationDbContext(DbContextOptions<IntegrationDbContext> options) : base(options) { }

        public DbSet<MondayItemMapping> MondayItemMappings => Set<MondayItemMapping>();
        public DbSet<SyncLog> SyncLogs => Set<SyncLog>();
        public DbSet<OdcanitCase> OdcanitMockCases => Set<OdcanitCase>();
        public DbSet<AllowedTik> AllowedTiks => Set<AllowedTik>();
        public DbSet<MondayHearingApprovalState> MondayHearingApprovalStates => Set<MondayHearingApprovalState>();
        public DbSet<NispahAuditLog> NispahAuditLogs => Set<NispahAuditLog>();
        public DbSet<NispahDeduplication> NispahDeduplications => Set<NispahDeduplication>();
        public DbSet<HearingNearestSnapshot> HearingNearestSnapshots => Set<HearingNearestSnapshot>();
        public DbSet<SyncFailure> SyncFailures => Set<SyncFailure>();
        public DbSet<SyncRunLock> SyncRunLocks => Set<SyncRunLock>();
        public DbSet<ListenerState> ListenerStates => Set<ListenerState>();
        public DbSet<EmailAlertDedup> EmailAlertDedups => Set<EmailAlertDedup>();
        public DbSet<SyncRunMetric> SyncRunMetrics => Set<SyncRunMetric>();
        public DbSet<MondayDocumentImport> MondayDocumentImports => Set<MondayDocumentImport>();
        public DbSet<NispahWriteLog> NispahWriteLogs => Set<NispahWriteLog>();
        public DbSet<CaseAnnexWriteState> CaseAnnexWriteStates => Set<CaseAnnexWriteState>();
        public DbSet<HearingBackfillApr2026> HearingBackfillApr2026 => Set<HearingBackfillApr2026>();
        public DbSet<VoicenterApiRequestLog> VoicenterApiRequestLogs => Set<VoicenterApiRequestLog>();
        public DbSet<VoicenterQuotaWarningState> VoicenterQuotaWarningStates => Set<VoicenterQuotaWarningState>();
        public DbSet<VoicenterCallProcessingState> VoicenterCallProcessingStates => Set<VoicenterCallProcessingState>();
        public DbSet<NetCourtDecisionAlert> NetCourtDecisionAlerts => Set<NetCourtDecisionAlert>();
        public DbSet<NetCourtDecisionAlertState> NetCourtDecisionAlertStates => Set<NetCourtDecisionAlertState>();
        public DbSet<EmailAutomationMailboxState> EmailAutomationMailboxStates => Set<EmailAutomationMailboxState>();
        public DbSet<EmailAutomationLog> EmailAutomationLogs => Set<EmailAutomationLog>();
        public DbSet<EmailFilingMailboxState> EmailFilingMailboxStates => Set<EmailFilingMailboxState>();
        public DbSet<EmailFilingDiagnostic> EmailFilingDiagnostics => Set<EmailFilingDiagnostic>();
        public DbSet<EmailFilingCandidateDiagnostic> EmailFilingCandidateDiagnostics => Set<EmailFilingCandidateDiagnostic>();
        public DbSet<EmailFilingTargetDiagnostic> EmailFilingTargetDiagnostics => Set<EmailFilingTargetDiagnostic>();
        public DbSet<EmailFilingDedup> EmailFilingDedups => Set<EmailFilingDedup>();
        public DbSet<EmailFilingResolutionRun> EmailFilingResolutionRuns => Set<EmailFilingResolutionRun>();
        public DbSet<EmailFilingResolutionTarget> EmailFilingResolutionTargets => Set<EmailFilingResolutionTarget>();
        public DbSet<EmailFilingResolutionCandidate> EmailFilingResolutionCandidates => Set<EmailFilingResolutionCandidate>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MondayItemMapping>()
                .HasKey(m => m.Id);
            modelBuilder.Entity<MondayItemMapping>()
                .ToTable(t => t.HasCheckConstraint("CK_MondayItemMappings_TikCounter_Positive", "[TikCounter] > 0"));
            modelBuilder.Entity<MondayItemMapping>()
                .HasIndex(m => m.TikCounter)
                .IsUnique();
            modelBuilder.Entity<MondayItemMapping>()
                .HasIndex(m => m.MondayItemId);
            modelBuilder.Entity<MondayItemMapping>()
                .HasIndex(m => new { m.TikNumber, m.BoardId });
            modelBuilder.Entity<MondayItemMapping>()
                .Property(m => m.HearingChecksum)
                .HasMaxLength(128);
            modelBuilder.Entity<MondayItemMapping>()
                .Property(m => m.CreatedAtUtc)
                .HasDefaultValueSql("SYSUTCDATETIME()");

            modelBuilder.Entity<ListenerState>(b =>
            {
                b.ToTable("ListenerState");
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).ValueGeneratedNever();
            });

            modelBuilder.Entity<SyncLog>()
                .HasKey(l => l.Id);

            modelBuilder.Entity<AllowedTik>(b =>
            {
                b.ToTable("AllowedTik");
                b.HasKey(x => x.TikCounter);
                
                // TikCounter is NOT an identity / auto-generated column
                b.Property(x => x.TikCounter)
                 .ValueGeneratedNever();
            });

            modelBuilder.Entity<MondayHearingApprovalState>(b =>
            {
                b.ToTable("MondayHearingApprovalStates");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.BoardId, x.MondayItemId }).IsUnique();
            });

            modelBuilder.Entity<NispahAuditLog>(b =>
            {
                b.ToTable("NispahAuditLogs");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.CorrelationId);
                b.HasIndex(x => new { x.TikVisualID, x.CreatedAtUtc });
            });

            modelBuilder.Entity<NispahDeduplication>(b =>
            {
                b.ToTable("NispahDeduplications");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.TikVisualID, x.NispahTypeName, x.InfoHash }).IsUnique();
                b.HasIndex(x => x.CreatedAtUtc);
            });

            modelBuilder.Entity<NispahWriteLog>(b =>
            {
                b.ToTable("NispahWriteLogs");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.TikCounter, x.NispahType, x.SourceItemId, x.InfoHash }).IsUnique();
                b.HasIndex(x => x.CreatedAtUtc);
                b.Property(x => x.NispahType).HasMaxLength(128).IsRequired();
                b.Property(x => x.SourceKind).HasMaxLength(32).IsRequired();
                b.Property(x => x.InfoHash).HasMaxLength(64).IsRequired();
                b.Property(x => x.TikVisualId).HasMaxLength(64);
                b.Property(x => x.ErrorMessage).HasMaxLength(2000);
            });

            modelBuilder.Entity<HearingNearestSnapshot>(b =>
            {
                b.ToTable("HearingNearestSnapshots");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.TikCounter, x.BoardId }).IsUnique();
                b.HasIndex(x => x.MondayItemId);
            });

            modelBuilder.Entity<SyncFailure>(b =>
            {
                b.ToTable("SyncFailures");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.TikCounter, x.OccurredAtUtc });
                b.HasIndex(x => x.Resolved);
                b.HasIndex(x => x.RunId);
                b.Property(x => x.ErrorMessage).HasMaxLength(2000);
                b.Property(x => x.ErrorType).HasMaxLength(256);
                b.Property(x => x.Operation).HasMaxLength(128);
                b.Property(x => x.RunId).HasMaxLength(64);
                b.Property(x => x.StackTrace).HasMaxLength(4000);
            });

            modelBuilder.Entity<SyncRunLock>(b =>
            {
                b.ToTable("SyncRunLocks");
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).ValueGeneratedNever();
                b.Property(x => x.LockedByRunId).HasMaxLength(64);
            });

            modelBuilder.Entity<EmailAlertDedup>(b =>
            {
                b.ToTable("EmailAlertDedups");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.Fingerprint).IsUnique();
                b.HasIndex(x => x.LastSeenUtc);
                b.Property(x => x.Fingerprint).HasMaxLength(128).IsRequired();
                b.Property(x => x.ExceptionType).HasMaxLength(256);
                b.Property(x => x.Source).HasMaxLength(256);
                b.Property(x => x.Subject).HasMaxLength(512);
            });

            modelBuilder.Entity<SyncRunMetric>(b =>
            {
                b.ToTable("SyncRunMetrics");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.StartedAtUtc);
                b.HasIndex(x => x.RunId).IsUnique();
                b.Property(x => x.RunId).HasMaxLength(64).IsRequired();
                b.Property(x => x.DataSource).HasMaxLength(128);
            });

            modelBuilder.Entity<CaseAnnexWriteState>(b =>
            {
                b.ToTable("CaseAnnexWriteState");
                b.HasKey(x => x.TikCounter);
                b.Property(x => x.TikCounter).ValueGeneratedNever();
                b.Property(x => x.AccidentStoryAnnexWrittenRunId).HasMaxLength(64);
            });

            modelBuilder.Entity<MondayDocumentImport>(b =>
            {
                b.ToTable("MondayDocumentImports");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.MondayQuestionnaireItemId, x.ColumnId, x.AssetId }).IsUnique();
                b.HasIndex(x => x.Status);
                b.HasIndex(x => x.TikCounter);
                b.Property(x => x.ColumnId).HasMaxLength(128).IsRequired();
                b.Property(x => x.AssetId).HasMaxLength(128).IsRequired();
                b.Property(x => x.TikVisualID).HasMaxLength(64);
                b.Property(x => x.OriginalFileName).HasMaxLength(512).IsRequired();
                b.Property(x => x.InboxFilePath).HasMaxLength(1024);
                b.Property(x => x.OdcanitDestPath).HasMaxLength(1024);
                b.Property(x => x.ErrorMessage).HasMaxLength(2000);
                b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
                b.Property(x => x.UpdatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            });

            modelBuilder.Entity<OdcanitCase>(b =>
            {
                b.HasNoKey();
                b.ToView(null);
                b.Property(x => x.TikCounter);
                b.Property(x => x.TikNumber);
                b.Property(x => x.TikName);
                b.Property(x => x.ClientName);
                b.Property(x => x.StatusName);
                b.Property(x => x.TikOwner);
                b.Property(x => x.tsCreateDate);
                b.Property(x => x.tsModifyDate);
                b.Property(x => x.Notes);
                b.Property(x => x.ClientVisualID);
                b.Property(x => x.HozlapTikNumber);
                b.Property(x => x.ClientPhone);
                b.Property(x => x.ClientEmail);
                b.Property(x => x.EventDate);
                b.Property(x => x.RequestedClaimAmount).HasPrecision(18, 2);
            });

            modelBuilder.Entity<HearingBackfillApr2026>(b =>
            {
                b.ToTable("HearingBackfill_Apr2026");
                b.HasNoKey();
            });

            modelBuilder.Entity<VoicenterApiRequestLog>(b =>
            {
                b.ToTable("VoicenterApiRequestLogs");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.WeekStartUtc).HasDatabaseName("IX_VoicenterApiRequestLogs_WeekStartUtc");
                b.HasIndex(x => x.CreatedAtUtc).HasDatabaseName("IX_VoicenterApiRequestLogs_CreatedAtUtc");
                b.HasIndex(x => x.CallId).HasDatabaseName("IX_VoicenterApiRequestLogs_CallId");
                b.HasIndex(x => new { x.EndpointType, x.WeekStartUtc })
                 .HasDatabaseName("IX_VoicenterApiRequestLogs_EndpointType_WeekStartUtc");
                b.Property(x => x.EndpointType).HasMaxLength(32).IsRequired();
                b.Property(x => x.CallId).HasMaxLength(128);
                b.Property(x => x.ErrorMessage).HasMaxLength(2000);
                b.Property(x => x.CorrelationId).HasMaxLength(64);
            });

            modelBuilder.Entity<VoicenterQuotaWarningState>(b =>
            {
                b.ToTable("VoicenterQuotaWarningStates");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.WeekStartUtc, x.EndpointType }).IsUnique()
                 .HasDatabaseName("UX_VoicenterQuotaWarningStates_Week_Endpoint");
                b.Property(x => x.EndpointType).HasMaxLength(32).IsRequired();
            });

            modelBuilder.Entity<VoicenterCallProcessingState>(b =>
            {
                b.ToTable("VoicenterCallProcessingStates");
                b.HasKey(x => x.CallId);
                b.Property(x => x.CallId).HasMaxLength(128).ValueGeneratedNever();
                b.Property(x => x.Status).HasMaxLength(32).IsRequired();
                b.Property(x => x.TikVisualId).HasMaxLength(64);
                b.Property(x => x.LastError).HasMaxLength(2000);
                b.HasIndex(x => x.Status).HasDatabaseName("IX_VoicenterCallProcessingStates_Status");
                b.HasIndex(x => x.LastSeenUtc).HasDatabaseName("IX_VoicenterCallProcessingStates_LastSeenUtc");
            });

            modelBuilder.Entity<NetCourtDecisionAlert>(b =>
            {
                b.ToTable("NetCourtDecisionAlerts");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.DocumentIdentity).IsUnique();
                b.HasIndex(x => x.Status);
                b.HasIndex(x => x.CreatedAtUtc);
                b.Property(x => x.DocumentIdentity).HasMaxLength(128).IsRequired();
                b.Property(x => x.TikNumber).HasMaxLength(64);
                b.Property(x => x.Description).HasMaxLength(1000);
                b.Property(x => x.DecisionDesc).HasMaxLength(2000);
                b.Property(x => x.IntendedRecipientEmail).HasMaxLength(320);
                b.Property(x => x.ActualRecipientEmail).HasMaxLength(1000);
                b.Property(x => x.EmailMode).HasMaxLength(16).IsRequired();
                b.Property(x => x.Status).HasMaxLength(32).IsRequired();
                b.Property(x => x.ErrorMessage).HasMaxLength(2000);
            });

            modelBuilder.Entity<NetCourtDecisionAlertState>(b =>
            {
                b.ToTable("NetCourtDecisionAlertState");
                b.HasKey(x => x.Id);
                b.Property(x => x.Id).ValueGeneratedNever();
                b.Property(x => x.LastSeenCounter);
            });

            modelBuilder.Entity<EmailAutomationMailboxState>(b =>
            {
                b.ToTable("EmailAutomationMailboxStates");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.Mailbox, x.FolderId }).IsUnique();
                b.Property(x => x.Mailbox).HasMaxLength(320).IsRequired();
                b.Property(x => x.FolderId).HasMaxLength(256).IsRequired();
                b.Property(x => x.DeltaLink).HasColumnType("nvarchar(max)");
            });

            modelBuilder.Entity<EmailAutomationLog>(b =>
            {
                b.ToTable("EmailAutomationLogs");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
                b.HasIndex(x => new { x.Mailbox, x.GraphMessageId });
                b.HasIndex(x => x.CreatedAtUtc);
                b.Property(x => x.Mailbox).HasMaxLength(320).IsRequired();
                b.Property(x => x.RuleName).HasMaxLength(128);
                b.Property(x => x.InternetMessageId).HasMaxLength(1000);
                b.Property(x => x.GraphMessageId).HasMaxLength(512).IsRequired();
                b.Property(x => x.Subject).HasMaxLength(1000);
                b.Property(x => x.Sender).HasMaxLength(320);
                b.Property(x => x.DetectedCourtCaseNumber).HasMaxLength(64);
                b.Property(x => x.ResolvedTikNumber).HasMaxLength(64);
                b.Property(x => x.ResolvedTargetEmail).HasMaxLength(320);
                b.Property(x => x.ActualForwardTo).HasMaxLength(320);
                b.Property(x => x.TargetEmail).HasMaxLength(320);
                b.Property(x => x.Action).HasMaxLength(64).IsRequired();
                b.Property(x => x.IdempotencyKey).HasMaxLength(64);
                b.Property(x => x.ErrorMessage).HasMaxLength(2000);
            });

            modelBuilder.Entity<EmailFilingDiagnostic>(b =>
            {
                b.ToTable("EmailFilingDiagnostics");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.MessageFingerprint);
                b.HasIndex(x => x.CreatedAtUtc);
                b.HasIndex(x => x.FinalDecision);
                b.Property(x => x.Mailbox).HasMaxLength(320).IsRequired();
                b.Property(x => x.MessageFingerprint).HasMaxLength(64).IsRequired();
                b.Property(x => x.ProcessedContentFingerprint).HasMaxLength(64);
                b.Property(x => x.ObserverClassifications).HasMaxLength(256).IsRequired();
                b.Property(x => x.FinalDecision).HasMaxLength(64).IsRequired();
            });

            modelBuilder.Entity<EmailFilingMailboxState>(b =>
            {
                b.ToTable("EmailFilingMailboxStates");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.Mailbox, x.FolderId }).IsUnique();
                b.Property(x => x.Mailbox).HasMaxLength(320).IsRequired();
                b.Property(x => x.FolderId).HasMaxLength(256).IsRequired();
                b.Property(x => x.DeltaLink).HasColumnType("nvarchar(max)");
                b.Property(x => x.NextLink).HasColumnType("nvarchar(max)");
            });

            modelBuilder.Entity<EmailFilingCandidateDiagnostic>(b =>
            {
                b.ToTable("EmailFilingCandidateDiagnostics");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.CandidateType, x.ResolutionStatus });
                b.Property(x => x.CandidateType).HasMaxLength(16).IsRequired();
                b.Property(x => x.Source).HasMaxLength(16).IsRequired();
                b.Property(x => x.Candidate).HasMaxLength(64).IsRequired();
                b.Property(x => x.ResolutionStatus).HasMaxLength(32).IsRequired();
                b.Property(x => x.ResolvedTikNumber).HasMaxLength(64);
                b.HasOne(x => x.EmailFilingDiagnostic)
                    .WithMany(x => x.Candidates)
                    .HasForeignKey(x => x.EmailFilingDiagnosticId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<EmailFilingTargetDiagnostic>(b =>
            {
                b.ToTable("EmailFilingTargetDiagnostics");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.TikCounter);
                b.HasIndex(x => x.Decision);
                b.Property(x => x.TikNumber).HasMaxLength(64).IsRequired();
                b.Property(x => x.DedupResult).HasMaxLength(32).IsRequired();
                b.Property(x => x.Decision).HasMaxLength(64).IsRequired();
                b.HasOne(x => x.EmailFilingDiagnostic)
                    .WithMany(x => x.Targets)
                    .HasForeignKey(x => x.EmailFilingDiagnosticId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<EmailFilingResolutionRun>(b =>
            {
                b.ToTable("EmailFilingResolutionRuns");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.EmailFilingDiagnosticId).IsUnique();
                b.HasIndex(x => x.CreatedAtUtc);
                b.HasIndex(x => x.FinalResolutionClass);
                b.HasIndex(x => x.AgreementWithExistingAuthority);
                b.HasIndex(x => x.AuthorityDecisionClass);
                b.Property(x => x.AuthorityDecisionClass).HasMaxLength(48).IsRequired();
                b.Property(x => x.SourceTemplateKind).HasMaxLength(32).IsRequired();
                b.Property(x => x.FinalResolutionClass).HasMaxLength(48).IsRequired();
                b.Property(x => x.AgreementWithExistingAuthority).HasMaxLength(48).IsRequired();
                b.Property(x => x.ObserverErrorCategory).HasMaxLength(128);
                b.HasOne(x => x.EmailFilingDiagnostic)
                    .WithOne(x => x.ResolutionRun)
                    .HasForeignKey<EmailFilingResolutionRun>(x => x.EmailFilingDiagnosticId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<EmailFilingResolutionTarget>(b =>
            {
                b.ToTable("EmailFilingResolutionTargets");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.ResolutionRunId, x.TikCounter, x.TargetKind }).IsUnique();
                b.HasIndex(x => new { x.TargetKind, x.TikCounter });
                b.Property(x => x.TargetKind).HasMaxLength(32).IsRequired();
                b.HasOne(x => x.ResolutionRun)
                    .WithMany(x => x.Targets)
                    .HasForeignKey(x => x.ResolutionRunId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<EmailFilingResolutionCandidate>(b =>
            {
                b.ToTable("EmailFilingResolutionCandidates");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new
                {
                    x.ResolutionRunId,
                    x.TikCounter,
                    x.PrimaryEvidenceType,
                    x.CandidateStage
                }).IsUnique();
                b.HasIndex(x => new { x.PrimaryEvidenceType, x.TikCounter });
                b.Property(x => x.PrimaryEvidenceType).HasMaxLength(16).IsRequired();
                b.Property(x => x.CandidateStage).HasMaxLength(24).IsRequired();
                b.HasOne(x => x.ResolutionRun)
                    .WithMany(x => x.Candidates)
                    .HasForeignKey(x => x.ResolutionRunId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<EmailFilingDedup>(b =>
            {
                b.ToTable("EmailFilingDedups");
                b.HasKey(x => x.Id);
                b.HasIndex(x => new { x.MessageFingerprint, x.TikCounter }).IsUnique();
                b.Property(x => x.MessageFingerprint).HasMaxLength(64).IsRequired();
                b.Property(x => x.TikNumber).HasMaxLength(64).IsRequired();
                b.Property(x => x.Status).HasMaxLength(32).IsRequired();
                b.Property(x => x.LastErrorCategory).HasMaxLength(128);
                b.Property(x => x.RowVersion).IsRowVersion();
            });

            base.OnModelCreating(modelBuilder);
        }
    }
}


