namespace Odmon.Worker.Monday
{
    public class MondaySettings
    {
        public string? ApiToken { get; set; }
        public long BoardId { get; set; }
        public long CasesBoardId { get; set; }
        public string? ToDoGroupId { get; set; } = "topics";
        public string? TestGroupId { get; set; } = "topics";
        public string? ClientPhoneColumnId { get; set; } = "phone_mkwe10tx";
        public string? ClientEmailColumnId { get; set; } = "email_mkwefwgy";
        public string? CaseNumberColumnId { get; set; } = "text_mkwe19hn";
        public string? ClientNumberColumnId { get; set; } = "dropdown_mkxjrssr";
        /// <summary>
        /// Optional fallback text column for ClientNumber when the dropdown label does not exist.
        /// If configured, the raw ClientVisualID is written to this text column instead.
        /// </summary>
        public string? ClientNumberTextColumnId { get; set; }
        public string? ClaimNumberColumnId { get; set; } = "text_mkwjy5pg";
        public string? CaseOpenDateColumnId { get; set; } = "date4";
        public string? EventDateColumnId { get; set; } = "date_mkwj3780";
        public string? CaseCloseDateColumnId { get; set; } = "date_mkweqkjf";
        public string? RequestedClaimAmountColumnId { get; set; } = "numeric_mkxw7s29";
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
        public string? PolicyHolderAddressColumnId { get; set; } = "text_mkxer5d1";
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
        public string? PlaintiffAddressColumnId { get; set; } = "text_mm1b2eaz";
        public string? PlaintiffPhoneColumnId { get; set; } = "phone_mkwe10tx";
        public string? PlaintiffEmailColumnId { get; set; } = "email_mkwjy4rs";
        public string? DefendantNameColumnId { get; set; } = "text_mkxeabj2";
        public string? DefendantFaxColumnId { get; set; } = "text_mkxe2zay";
        public string? ThirdPartyDriverNameColumnId { get; set; } = "text_mkwj9bvj";
        public string? ThirdPartyDriverIdColumnId { get; set; } = "text_mkwjmad2";
        public string? ThirdPartyCarNumberColumnId { get; set; } = "text_mky2df4d";
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
        public string? ThirdPartyLawyerEmailColumnId { get; set; } = "email_mky2vqm3";
        /// <summary>פקס עו"ד צד ג' — third-party lawyer fax text column.</summary>
        public string? ThirdPartyLawyerFaxColumnId { get; set; } = "text_mkxe2zay";
        public string? CourtNameStatusColumnId { get; set; }
        public string? CourtCityColumnId { get; set; } = "text_mkxez28d";
        public string? CourtCaseNumberColumnId { get; set; } = "text_mkwj3kf4";
        public string? JudgeNameColumnId { get; set; } = "text_mkwjne8v";
        public string? HearingDateColumnId { get; set; } = "date_mkwjwmzq";
        public string? HearingHourColumnId { get; set; } = "hour_mkwjbwr";
        /// <summary>Hearing status column (פעיל / מבוטל / הועבר). ColumnId: color_mkzqbrta.</summary>
        public string? HearingStatusColumnId { get; set; } = "color_mkzqbrta";
        public string? AttorneyNameColumnId { get; set; } = "text_mkxeqj54";
        public string? DefenseStreetColumnId { get; set; } = "text_mkxwzxcq";
        public string? ClaimStreetColumnId { get; set; }
        public string? ComplaintReceivedDateColumnId { get; set; } = "date_mkxeapah";
        public string? CaseFolderIdColumnId { get; set; } = "text_mkxe3vhk";
        public string? TaskTypeStatusColumnId { get; set; } = "color_mkwyq310";
        public string? ResponsibleTextColumnId { get; set; } = "text_mkxz6j9y";
        public string? DocumentTypeStatusColumnId { get; set; } = "color_mkxhq546";
        /// <summary>נסיבות התאונה בקצרה — short accident circumstances text column.</summary>
        public string? ShortAccidentCircumstancesColumnId { get; set; } = "text_mky1vzgg";
        /// <summary>זיהוי נוסף — additional identification text column.</summary>
        public string? AdditionalIdentificationColumnId { get; set; } = "text_mm1gvfd5";
        /// <summary>גרסאות תביעה — claim versions long_text column.</summary>
        public string? ClaimVersionsColumnId { get; set; } = "long_text_mm1gsvg0";
        /// <summary>תאריך אחרון להגשת כתב הטענות — pleading deadline date column.</summary>
        public string? PleadingDeadlineDateColumnId { get; set; } = "date_mm1gex5r";
        /// <summary>גרסאות הגנה — defense versions long_text column.</summary>
        public string? DefenseVersionsColumnId { get; set; } = "long_text_mm1gxq01";
        /// <summary>חברות ביטוח 1 — first insurance company name text column.</summary>
        public string? InsuranceCompany1ColumnId { get; set; } = "text_mm1qnbwn";
        /// <summary>כתובת חברת ביטוח 1 — first insurance company address text column.</summary>
        public string? InsuranceCompany1AddressColumnId { get; set; } = "text_mm1q8a83";
        /// <summary>חברת ביטוח 2 — second insurance company text column.</summary>
        public string? InsuranceCompany2ColumnId { get; set; } = "text_mm1gy0q4";
        /// <summary>כתובת חברת ביטוח 2 — second insurance company address text column.</summary>
        public string? InsuranceCompany2AddressColumnId { get; set; } = "text_mm1gmta2";
        /// <summary>סוג הליך — proceeding type text column.</summary>
        public string? ProceedingTypeColumnId { get; set; } = "text_mm1gsp8k";
        /// <summary>סכום לתשלום — payment due amount numeric column.</summary>
        public string? PaymentDueAmountColumnId { get; set; } = "numeric_mm1gj81h";
        /// <summary>סכום תביעה צד ג — third-party claim amount numeric column.</summary>
        public string? ThirdPartyClaimAmountColumnId { get; set; } = "numeric_mky2m5a1";
        /// <summary>דמי כינון — reconstruction fee numeric column.</summary>
        public string? ReconstructionFeeAmountColumnId { get; set; } = "numeric_mky2yr0y";
        /// <summary>השתתפות עצמית לנזק — deductible damage numeric column.</summary>
        public string? DeductibleDamageAmountColumnId { get; set; } = "numeric_mky2ywrf";
        /// <summary>תגמולי ביטוח — insurance benefits numeric column.</summary>
        public string? InsuranceBenefitsAmountColumnId { get; set; } = "numeric_mkza3p82";

        /// <summary>
        /// If true, inactive Monday items may be revived (new item created, mapping updated).
        /// Default false: inactive items are skipped with a warning, no revive.
        /// </summary>
        public bool ReviveInactiveItems { get; set; } = false;
    }
}

