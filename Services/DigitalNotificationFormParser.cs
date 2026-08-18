using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public sealed partial class DigitalNotificationFormParser
    {
        private static readonly string[] ExplicitClaimNumberLabels =
            ["מספר תביעה", "מס' תביעה", "מס׳ תביעה"];
        private static readonly string[] ReportNumberLabels =
            ["מס' דיווח", "מס׳ דיווח", "מספר דיווח"];
        private static readonly string[] EventDateLabels = ["תאריך אירוע"];
        private static readonly string[] PolicyNumberLabels = ["מספר פוליסה", "מס' פוליסה", "מס׳ פוליסה"];
        private static readonly string[] PolicyHolderNameLabels = ["שם בעל הפוליסה", "שם בעל פוליסה"];
        private static readonly string[] PolicyHolderIdLabels =
            ["תעודת זהות בעל הפוליסה", "תעודת זהות בעל פוליסה", "ת.ז. בעל פוליסה", "ת.ז בעל פוליסה"];
        private static readonly string[] PolicyHolderPhoneLabels =
            ["טלפון בעל הפוליסה", "טלפון בעל פוליסה", "סלולרי בעל הפוליסה", "סלולרי בעל פוליסה"];
        private static readonly string[] DriverNameLabels = ["שם הנהג", "שם נהג"];
        private static readonly string[] DriverIdLabels =
            ["תעודת זהות הנהג", "תעודת זהות נהג", "ת.ז. נהג", "ת.ז נהג"];
        private static readonly string[] DriverPhoneLabels =
            ["טלפון הנהג", "טלפון נהג", "סלולרי הנהג", "סלולרי נהג"];
        private static readonly string[] MainCarNumberLabels =
            ["מספר רכב מבוטח", "מספר רכב ראשי", "מספר רישוי", "מספר רכב"];
        private static readonly string[] ThirdPartyCarNumberLabels =
            ["מספר רישוי רכב צד ג'", "מספר רישוי רכב ג'", "מספר רישוי צד ג'", "מספר רכב צד ג'",
             "מספר רישוי רכב צד ג׳", "מספר רישוי רכב ג׳", "מספר רישוי צד ג׳", "מספר רכב צד ג׳"];

        private static readonly string[] AllLabels =
            ExplicitClaimNumberLabels
                .Concat(ReportNumberLabels)
                .Concat(EventDateLabels)
                .Concat(PolicyNumberLabels)
                .Concat(PolicyHolderNameLabels)
                .Concat(PolicyHolderIdLabels)
                .Concat(PolicyHolderPhoneLabels)
                .Concat(DriverNameLabels)
                .Concat(DriverIdLabels)
                .Concat(DriverPhoneLabels)
                .Concat(MainCarNumberLabels)
                .Concat(ThirdPartyCarNumberLabels)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(label => label.Length)
                .ToArray();
        private static readonly CaseIntakeTextFieldExtractor TextExtractor = new(AllLabels);

        public NotificationFormFields Parse(string extractedText, OdcanitCaseDocument document)
        {
            ArgumentNullException.ThrowIfNull(extractedText);
            ArgumentNullException.ThrowIfNull(document);

            var lines = TextExtractor.NormalizeLines(extractedText);
            var claimNumber = CaseIntakeClaimNumberResolver.Resolve(
                document,
                TextExtractor.Extract(lines, ExplicitClaimNumberLabels),
                TextExtractor.Extract(lines, ReportNumberLabels));

            return new NotificationFormFields(
                ClaimNumber: claimNumber,
                EventDate: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, EventDateLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateDate),
                PolicyNumber: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, PolicyNumberLabels),
                    document,
                    raw => CaseIntakeFieldValidators.ValidateNumber(raw, "Policy number")),
                PolicyHolderName: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, PolicyHolderNameLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateName),
                PolicyHolderId: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, PolicyHolderIdLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateIdentifierNumber),
                PolicyHolderPhone: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, PolicyHolderPhoneLabels),
                    document,
                    CaseIntakeFieldValidators.ValidatePhone),
                DriverName: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, DriverNameLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateName),
                DriverId: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, DriverIdLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateIdentifierNumber),
                DriverPhone: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, DriverPhoneLabels),
                    document,
                    CaseIntakeFieldValidators.ValidatePhone),
                MainCarNumber: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, MainCarNumberLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateVehicleNumber),
                ThirdPartyCarNumber: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, ThirdPartyCarNumberLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateVehicleNumber));
        }
    }
}
