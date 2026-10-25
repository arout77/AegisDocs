using ReceiptCapture.Core.Redaction;

namespace ReceiptCapture.Core.Compliance;

/// <summary>One kind of item a profile looks for, how serious a hit is, and what it means in plain words.</summary>
public sealed record ProfileCheck(SensitiveKind Kind, FindingSeverity Severity, string Explanation);

/// <summary>
/// A named set of things to look for (HIPAA identifiers, card data, ...). A profile is a practical checklist,
/// not a legal standard: the report always says what it can't detect.
/// </summary>
public sealed class ComplianceProfile
{
    public ComplianceProfile(string id, string name, string summary, IReadOnlyList<ProfileCheck> checks, IReadOnlyList<string> notChecked)
    {
        Id = id;
        Name = name;
        Summary = summary;
        Checks = checks;
        NotChecked = notChecked;
    }

    public string Id { get; }
    public string Name { get; }
    public string Summary { get; }
    public IReadOnlyList<ProfileCheck> Checks { get; }

    /// <summary>Things this profile cares about that a text scan cannot find. Shown in the app and in every report.</summary>
    public IReadOnlyList<string> NotChecked { get; }

    public FindingSeverity SeverityOf(SensitiveKind kind)
        => Checks.FirstOrDefault(c => c.Kind == kind)?.Severity ?? FindingSeverity.Medium;

    /// <summary>The finder settings for this profile: exactly its kinds on, everything else off, plus the user's own words.</summary>
    public FindOptions BuildOptions(IReadOnlyList<string>? customTerms = null)
    {
        bool On(SensitiveKind k) => Checks.Any(c => c.Kind == k);
        return new FindOptions
        {
            Ssn = On(SensitiveKind.Ssn),
            Phone = On(SensitiveKind.Phone),
            Email = On(SensitiveKind.Email),
            CreditCard = On(SensitiveKind.CreditCard),
            DateOfBirth = On(SensitiveKind.DateOfBirth),
            Ein = On(SensitiveKind.Ein),
            BankAccount = On(SensitiveKind.BankAccount),
            RoutingNumber = On(SensitiveKind.RoutingNumber),
            DriversLicense = On(SensitiveKind.DriversLicense),
            Passport = On(SensitiveKind.Passport),
            Address = On(SensitiveKind.Address),
            Mrn = On(SensitiveKind.Mrn),
            HealthPlanId = On(SensitiveKind.HealthPlanId),
            RxNumber = On(SensitiveKind.RxNumber),
            ZipCode = On(SensitiveKind.ZipCode),
            IpAddress = On(SensitiveKind.IpAddress),
            Url = On(SensitiveKind.Url),
            Fax = On(SensitiveKind.Fax),
            AgeOver89 = On(SensitiveKind.AgeOver89),
            AllDates = On(SensitiveKind.Date),
            CardCvv = On(SensitiveKind.CardCvv),
            CardExpiry = On(SensitiveKind.CardExpiry),
            CardTrackData = On(SensitiveKind.CardTrackData),
            CustomTerms = customTerms ?? Array.Empty<string>(),
        };
    }
}

public static class ComplianceProfiles
{
    private const FindingSeverity High = FindingSeverity.High, Medium = FindingSeverity.Medium, Low = FindingSeverity.Low;

    public static readonly ComplianceProfile Hipaa = new(
        "hipaa", "HIPAA Safe Harbor identifiers",
        "Looks for the kinds of identifier that HIPAA's Safe Harbor method says to remove before health information counts as de-identified.",
        new ProfileCheck[]
        {
            new(SensitiveKind.Ssn, High, "Social Security numbers"),
            new(SensitiveKind.Mrn, High, "Medical record numbers, found by a label such as MRN or Patient ID"),
            new(SensitiveKind.HealthPlanId, High, "Health plan and member numbers, found by a label such as Member ID, plus Medicare (MBI) numbers"),
            new(SensitiveKind.RxNumber, Medium, "Prescription numbers, found by a label such as Prescription number or Rx #"),
            new(SensitiveKind.CreditCard, High, "Card numbers (account numbers)"),
            new(SensitiveKind.BankAccount, High, "Bank account numbers, found by a label such as Account No."),
            new(SensitiveKind.DriversLicense, High, "Driver's license and other license numbers, found by a label"),
            new(SensitiveKind.Passport, High, "Passport numbers, found by a label"),
            new(SensitiveKind.DateOfBirth, Medium, "Dates of birth, found by a label such as DOB"),
            new(SensitiveKind.AgeOver89, Medium, "Ages over 89"),
            new(SensitiveKind.Address, Medium, "Street addresses and P.O. boxes, with the city, state and ZIP that follow"),
            new(SensitiveKind.ZipCode, Medium, "ZIP codes after a state or labelled ZIP"),
            new(SensitiveKind.Phone, Medium, "Telephone numbers"),
            new(SensitiveKind.Fax, Medium, "Fax numbers, found by a label such as Fax:"),
            new(SensitiveKind.Email, Medium, "Email addresses"),
            new(SensitiveKind.IpAddress, Medium, "IP addresses"),
            new(SensitiveKind.Date, Low, "Any other date (HIPAA counts every part of a date except the year)"),
            new(SensitiveKind.Url, Low, "Web addresses"),
        },
        new[]
        {
            "Names. Add the names you want checked in the box below; the scan can't recognise a name on its own.",
            "Photographs and comparable images, and biometric identifiers such as fingerprints and voiceprints.",
            "Barcodes and QR codes. An ID card's barcode repeats the details printed on it.",
            "Vehicle identifiers and license plates, and device identifiers and serial numbers.",
            "Cities, counties and other places smaller than a state, when written without a street address or ZIP code.",
            "Any other unique number, code or characteristic that could identify a person.",
            "Health details written out in ordinary sentences. Safe Harbor also requires that you have no actual knowledge the remaining information could identify someone.",
        });

    public static readonly ComplianceProfile Pci = new(
        "pci", "PCI DSS cardholder data",
        "Looks for payment card data of the kind PCI DSS protects: card numbers, security codes, expiry dates and magnetic-stripe data.",
        new ProfileCheck[]
        {
            new(SensitiveKind.CardTrackData, High, "Magnetic-stripe (track) data. PCI DSS says this must not be kept after a payment is authorized"),
            new(SensitiveKind.CardCvv, High, "Card security codes (CVV, CVC, CID). PCI DSS says these must not be kept after a payment is authorized"),
            new(SensitiveKind.CreditCard, High, "Card numbers that pass the card checksum; the card brand is shown in the report"),
            new(SensitiveKind.CardExpiry, Medium, "Expiry dates, found by a label (Exp, Valid thru) or right after a card number"),
        },
        new[]
        {
            "PINs and PIN blocks, cardholder names and service codes.",
            "Card images and photographs.",
            "Card numbers split across lines or misread by the text reader.",
            "Your systems. This looks at documents only; it does not assess whether you meet PCI DSS.",
        });

    public static readonly ComplianceProfile Glba = new(
        "glba", "Financial (GLBA-style) personal information",
        "Looks for the personal and account details financial firms typically have to protect. GLBA doesn't publish an identifier list, so this is a practical set.",
        new ProfileCheck[]
        {
            new(SensitiveKind.Ssn, High, "Social Security numbers"),
            new(SensitiveKind.CreditCard, High, "Card numbers"),
            new(SensitiveKind.BankAccount, High, "Bank account numbers, found by a label such as Account No."),
            new(SensitiveKind.RoutingNumber, High, "Routing numbers, found by a label such as Routing or ABA"),
            new(SensitiveKind.DriversLicense, High, "Driver's license numbers, found by a label"),
            new(SensitiveKind.Passport, High, "Passport numbers, found by a label"),
            new(SensitiveKind.Ein, Medium, "Employer and tax ID numbers (EIN)"),
            new(SensitiveKind.DateOfBirth, Medium, "Dates of birth, found by a label such as DOB"),
            new(SensitiveKind.Address, Medium, "Street addresses and P.O. boxes"),
            new(SensitiveKind.Phone, Medium, "Telephone numbers"),
            new(SensitiveKind.Email, Medium, "Email addresses"),
        },
        new[]
        {
            "Names. Add the names you want checked in the box below.",
            "Income, balances, credit history and other financial details written in ordinary text.",
            "Loan, application and policy numbers that have no label the scan recognises.",
            "Signatures, photographs and scanned ID cards. An ID card's barcode repeats the details printed on it.",
        });

    public static readonly ComplianceProfile GeneralPii = new(
        "pii", "General personal information",
        "A broad everyday check for personal details: government IDs, contact details, dates of birth and financial account numbers.",
        new ProfileCheck[]
        {
            new(SensitiveKind.Ssn, High, "Social Security numbers"),
            new(SensitiveKind.Passport, High, "Passport numbers, found by a label"),
            new(SensitiveKind.DriversLicense, High, "Driver's license numbers, found by a label"),
            new(SensitiveKind.CreditCard, High, "Card numbers"),
            new(SensitiveKind.BankAccount, High, "Bank account numbers, found by a label such as Account No."),
            new(SensitiveKind.RoutingNumber, Medium, "Routing numbers, found by a label such as Routing or ABA"),
            new(SensitiveKind.Ein, Medium, "Employer and tax ID numbers (EIN)"),
            new(SensitiveKind.DateOfBirth, Medium, "Dates of birth, found by a label such as DOB"),
            new(SensitiveKind.Address, Medium, "Street addresses and P.O. boxes"),
            new(SensitiveKind.Phone, Medium, "Telephone numbers"),
            new(SensitiveKind.Email, Medium, "Email addresses"),
        },
        new[]
        {
            "Names. Add the names you want checked in the box below.",
            "Photographs, signatures and scanned ID cards. An ID card's barcode repeats the details printed on it.",
            "Personal details written out in ordinary sentences.",
        });

    public static readonly IReadOnlyList<ComplianceProfile> All = new[] { Hipaa, Pci, Glba, GeneralPii };

    public static ComplianceProfile? ById(string id)
        => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
