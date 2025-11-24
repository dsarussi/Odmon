using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odmon.Worker.Models
{
    public class OdcanitCase
    {
        public int SideCounter { get; set; }
        public int TikCounter { get; set; }
        public string TikNumber { get; set; } = string.Empty;
        public string TikName { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
        public string StatusName { get; set; } = string.Empty;
        public int? TikOwner { get; set; }
        public DateTime tsCreateDate { get; set; }
        public DateTime tsModifyDate { get; set; }
        public DateTime? TikCloseDate { get; set; }
        public DateTime? StatusChangedDate { get; set; }
        public DateTime? HozlapOpenDate { get; set; }
        public string? LishkaName { get; set; }
        public string? MautName { get; set; }
        public string? TeamName { get; set; }
        public string? Referant { get; set; }
        public int? StageCode { get; set; }
        public string? StageName { get; set; }
        public string? HalichName { get; set; }
        public string? TiukName { get; set; }
        public DateTime? TiukDate { get; set; }
        [NotMapped]
        public decimal? TikBalance { get; set; }
        public DateTime? TikBalanceCalcDate { get; set; }
        [NotMapped]
        public decimal? HozlapBalance { get; set; }
        public DateTime? HozlapBalanceCalcDate { get; set; }
        public string? Notes { get; set; }
        public string? ClientVisualID { get; set; }
        public string? HozlapTikNumber { get; set; }
        [NotMapped]
        public string? ClientPhone { get; set; }
        [NotMapped]
        public string? ClientEmail { get; set; }
        [NotMapped]
        public string? ClientTaxId { get; set; }
        [NotMapped]
        public DateTime? EventDate { get; set; }
        [NotMapped]
        public decimal? RequestedClaimAmount { get; set; }
        [NotMapped]
        public decimal? ProvenClaimAmount { get; set; }
        [NotMapped]
        public decimal? DirectDamageAmount { get; set; }
        [NotMapped]
        public decimal? LossOfValueAmount { get; set; }
        [NotMapped]
        public decimal? OtherLossesAmount { get; set; }
        [NotMapped]
        public decimal? PaymentDueAmount { get; set; }
        [NotMapped]
        public decimal? AppraiserFeeAmount { get; set; }
        [NotMapped]
        public decimal? JudgmentAmount { get; set; }
        [NotMapped]
        public decimal? CourtFeeTotal { get; set; }
        [NotMapped]
        public decimal? CourtFeePartOne { get; set; }
        [NotMapped]
        public string? MainCarNumber { get; set; }
        [NotMapped]
        public string? SecondCarNumber { get; set; }
        [NotMapped]
        public string? ThirdPartyCarNumber { get; set; }
        [NotMapped]
        public string? PolicyHolderName { get; set; }
        [NotMapped]
        public string? PolicyHolderId { get; set; }
        [NotMapped]
        public string? PolicyHolderAddress { get; set; }
        [NotMapped]
        public string? PolicyHolderPhone { get; set; }
        [NotMapped]
        public string? PolicyHolderEmail { get; set; }
        [NotMapped]
        public string? DriverName { get; set; }
        [NotMapped]
        public string? DriverId { get; set; }
        [NotMapped]
        public string? DriverPhone { get; set; }
        [NotMapped]
        public string? WitnessName { get; set; }
        [NotMapped]
        public string? WitnessPhone { get; set; }
        [NotMapped]
        public string? PlaintiffName { get; set; }
        [NotMapped]
        public string? PlaintiffId { get; set; }
        [NotMapped]
        public string? PlaintiffAddress { get; set; }
        [NotMapped]
        public string? PlaintiffPhone { get; set; }
        [NotMapped]
        public string? PlaintiffEmail { get; set; }
        [NotMapped]
        public string? DefendantName { get; set; }
        [NotMapped]
        public string? DefendantId { get; set; }
        [NotMapped]
        public string? DefendantAddress { get; set; }
        [NotMapped]
        public string? DefendantPhone { get; set; }
        [NotMapped]
        public string? DefendantFax { get; set; }
        [NotMapped]
        public string? AdditionalDefendants { get; set; }
        [NotMapped]
        public string? ThirdPartyDriverName { get; set; }
        [NotMapped]
        public string? ThirdPartyDriverId { get; set; }
        [NotMapped]
        public string? ThirdPartyPhone { get; set; }
        [NotMapped]
        public string? ThirdPartyEmployerName { get; set; }
        [NotMapped]
        public string? ThirdPartyEmployerId { get; set; }
        [NotMapped]
        public string? ThirdPartyEmployerAddress { get; set; }
        [NotMapped]
        public string? ThirdPartyLawyerName { get; set; }
        [NotMapped]
        public string? ThirdPartyLawyerAddress { get; set; }
        [NotMapped]
        public string? ThirdPartyLawyerPhone { get; set; }
        [NotMapped]
        public string? ThirdPartyLawyerEmail { get; set; }
        [NotMapped]
        public string? ThirdPartyInsurerName { get; set; }
        [NotMapped]
        public string? InsuranceCompanyId { get; set; }
        [NotMapped]
        public string? InsuranceCompanyAddress { get; set; }
        [NotMapped]
        public string? InsuranceCompanyEmail { get; set; }
        [NotMapped]
        public string? CourtName { get; set; }
        [NotMapped]
        public string? CourtCity { get; set; }
        [NotMapped]
        public string? CourtCaseNumber { get; set; }
        [NotMapped]
        public string? JudgeName { get; set; }
        [NotMapped]
        public DateTime? HearingDate { get; set; }
        [NotMapped]
        public TimeSpan? HearingTime { get; set; }
        [NotMapped]
        public string? ClientAddress { get; set; }
        [NotMapped]
        public string? GarageName { get; set; }
        [NotMapped]
        public string? AttorneyName { get; set; }
        [NotMapped]
        public string? DefenseStreet { get; set; }
        [NotMapped]
        public string? ClaimStreet { get; set; }
        [NotMapped]
        public DateTime? ComplaintReceivedDate { get; set; }
        [NotMapped]
        public string? CaseFolderId { get; set; }
    }
}

