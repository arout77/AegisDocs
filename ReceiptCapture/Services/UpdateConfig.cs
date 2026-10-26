namespace ReceiptCapture.Services;

/// <summary>
/// Where the program looks for updates and which key it trusts. Both are empty until you set them up (README, "Updates"),
/// and while they are empty the program never contacts anyone and says updates aren't set up.
/// </summary>
public static class UpdateConfig
{
    /// <summary>The https address of latest.json, for example "https://updates.example.com/aegisdocs/latest.json".</summary>
    public const string ManifestUrl = "";

    /// <summary>The PUBLIC signing key printed by <c>tools/UpdateSigner keygen</c>. Safe to keep in the source code.</summary>
    public const string PublicKey = "";
}
