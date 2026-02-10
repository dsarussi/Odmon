namespace Odmon.Worker.Monday
{
    public class MondaySettings
    {
        public string? ApiToken { get; set; }
        public long BoardId { get; set; }
        public long CasesBoardId { get; set; }
        public string? ToDoGroupId { get; set; }
        public string? TestGroupId { get; set; }
        public string? ClientPhoneColumnId { get; set; } = "phone_mkwe10tx";
        public string? ClientEmailColumnId { get; set; } = "email_mkwefwgy";
        public string? CaseNumberColumnId { get; set; } = "text_mkwe19hn";
        public string? ClientNumberColumnId { get; set; } = "dropdown_mkxjrssr";
        public string? ClaimNumberColumnId { get; set; } = "text_mkwjy5pg";
        public string? CaseOpenDateColumnId { get; set; } = "date4";
        public string? EventDateColumnId { get; set; } = "date_mkwj3780";
        public string? CaseCloseDateColumnId { get; set; } = "date_mkweqkjf";
        public string? RequestedClaimAmountColumnId { get; set; } = "numeric_mkxw7s29";
        public string? ProvenClaimAmountColumnId { get; set; } = "numeric_mkwjcrwk";
        public string? JudgmentAmountColumnId { get; set; } = "numeric_mkwj6mnw";
        public string? AppraiserFeeAmountColumnId { get; set; } = "numeric_mky2n7hz";
        public string? DirectDamageAmountColumnId { get; set; } = "numeric_mky1jccw";
        public string? OtherLossesAmountColumnId { get; set; } = "numeric_mky1tv4r";
        public string? LossOfValueAmountColumnId { get; set; } = "numeric_mky23vbb";
        public string? ResidualValueAmountColumnId { get; set; } = "numeric_mkzjw4z7";
        public string? NotesColumnId { get; set; } = "long_text_mkwe5h8v";
        public string? CaseStatusColumnId { get; set; } = "color_mkwefnbx";
        public string? ClientAddressColumnId { get; set; } = "text_mkwjcc69";
        public string? ClientTaxIdColumnId { get; set; } = "text_mkwjzsvg";
        public string? PolicyHolderNameColumnId { get; set; } = "text_mky27a51";
        public string? PolicyHolderIdColumnId { get; set; } = "text_mkwjqdb4";
        public string? PolicyHolderAddressColumnId { get; set; } = "text_mkwjan1q";
        public string? PolicyHolderPhoneColumnId { get; set; } = "phone_mkwjzg9";
        public string? PolicyHolderEmailColumnId { get; set; } = "email_mkwjbh2t";
        public string? MainCarNumberColumnId { get; set; } = "text_mkwjnwh7";
        public string? DriverNameColumnId { get; set; } = "text_mkwja7cv";
        public string? DriverIdColumnId { get; set; } = "text_mkwjbtre";
        public string? DriverPhoneColumnId { get; set; } = "phone_mkwj7fak";
        public string? WitnessNameColumnId { get; set; } = "text_mkwjt62y";
        public string? AdditionalDefendantsColumnId { get; set; } = "long_text_mkwjhngq";
        public string? PlaintiffNameColumnId { get; set; } = "text_mkwj5k8e";
        public string? PlaintiffIdColumnId { get; set; } = "text_mkwj82zd";
        public string? PlaintiffAddressColumnId { get; set; } = "text_mkwjvvp6";
        public string? PlaintiffPhoneColumnId { get; set; } = "phone_mkwe10tx";
        public string? PlaintiffEmailColumnId { get; set; } = "email_mkwjy4rs";
        public string? DefendantNameColumnId { get; set; } = "text_mkxeabj2";
        public string? DefendantFaxColumnId { get; set; } = "text_mkxe2zay";
        public string? ThirdPartyDriverNameColumnId { get; set; } = "text_mkwj9bvj";
        public string? ThirdPartyDriverIdColumnId { get; set; } = "text_mkwjmad2";
        public string? ThirdPartyCarNumberColumnId { get; set; } = "text_mkwj5jpn";
        public string? ThirdPartyPhoneColumnId { get; set; } = "phone_mkwj9a3a";
        public string? ThirdPartyInsurerStatusColumnId { get; set; } = "color_mkwjz9mp";
        public string? InsuranceCompanyIdColumnId { get; set; } = "text_mkwjmpex";
        public string? InsuranceCompanyAddressColumnId { get; set; } = "text_mkwjnvdr";
        public string? InsuranceCompanyEmailColumnId { get; set; } = "email_mkwjv6zw";
        public string? ThirdPartyEmployerNameColumnId { get; set; } = "text_mkwj6b";
        public string? ThirdPartyEmployerIdColumnId { get; set; } = "text_mkwjfkbm";
        public string? ThirdPartyEmployerAddressColumnId { get; set; } = "text_mkwjgpd2";
        public string? ThirdPartyLawyerNameColumnId { get; set; } = "text_mkwj1w08";
        public string? ThirdPartyLawyerAddressColumnId { get; set; } = "text_mkwjdzdg";
        public string? ThirdPartyLawyerPhoneColumnId { get; set; } = "phone_mkwjfge2";
        public string? ThirdPartyLawyerEmailColumnId { get; set; } = "email_mkwj4mmk";
        public string? CourtNameStatusColumnId { get; set; } = "color_mkwj24j";
        public string? CourtCityColumnId { get; set; } = "text_mkxez28d";
        public string? CourtCaseNumberColumnId { get; set; } = "text_mkwj3kf4";
        public string? JudgeNameColumnId { get; set; } = "text_mkwjne8v";
        public string? HearingDateColumnId { get; set; } = "date_mkwjwmzq";
        public string? HearingHourColumnId { get; set; } = "hour_mkwjbwr";
        /// <summary>Hearing status column (פעיל / מבוטל / הועבר). ColumnId: color_mkzqbrta.</summary>
        public string? HearingStatusColumnId { get; set; } = "color_mkzqbrta";
        public string? AttorneyNameColumnId { get; set; } = "text_mkxeqj54";
        public string? DefenseStreetColumnId { get; set; } = "text_mkxer5d1";
        public string? ClaimStreetColumnId { get; set; } = "text_mkxwzxcq";
        public string? ComplaintReceivedDateColumnId { get; set; } = "date_mkxeapah";
        public string? CaseFolderIdColumnId { get; set; } = "text_mkxe3vhk";
        public string? TaskTypeStatusColumnId { get; set; } = "color_mkwyq310";
        public string? ResponsibleTextColumnId { get; set; } = "text_mkxz6j9y";
        public string? DocumentTypeStatusColumnId { get; set; } = "color_mkxhq546";

        /// <summary>
        /// If true, inactive Monday items may be revived (new item created, mapping updated).
        /// Default false: inactive items are skipped with a warning, no revive.
        /// </summary>
        public bool ReviveInactiveItems { get; set; } = false;
    }
}

