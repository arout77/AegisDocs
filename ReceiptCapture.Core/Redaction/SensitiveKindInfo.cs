namespace ReceiptCapture.Core.Redaction;

/// <summary>Plain-language names for each kind of sensitive item, shared by the Redact tab and the compliance scanner.</summary>
public static class SensitiveKindInfo
{
    public static string DisplayName(SensitiveKind kind) => kind switch
    {
        SensitiveKind.CustomTerm => "Your term",
        SensitiveKind.CardTrackData => "Card track data",
        SensitiveKind.CreditCard => "Card number",
        SensitiveKind.Ssn => "SSN",
        SensitiveKind.Ein => "EIN",
        SensitiveKind.RoutingNumber => "Routing number",
        SensitiveKind.BankAccount => "Bank account",
        SensitiveKind.Passport => "Passport",
        SensitiveKind.DriversLicense => "Driver's license",
        SensitiveKind.Mrn => "Medical record number",
        SensitiveKind.HealthPlanId => "Health plan ID",
        SensitiveKind.DateOfBirth => "Date of birth",
        SensitiveKind.CardCvv => "Card security code",
        SensitiveKind.CardExpiry => "Card expiry date",
        SensitiveKind.Email => "Email",
        SensitiveKind.Url => "Web address",
        SensitiveKind.IpAddress => "IP address",
        SensitiveKind.Fax => "Fax number",
        SensitiveKind.Phone => "Phone",
        SensitiveKind.AgeOver89 => "Age over 89",
        SensitiveKind.Date => "Date",
        SensitiveKind.Address => "Address",
        SensitiveKind.ZipCode => "ZIP code",
        _ => "Item",
    };
}
