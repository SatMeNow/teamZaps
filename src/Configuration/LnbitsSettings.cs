namespace teamZaps.Configuration;

public class LnbitsSettings
{
    public const string SectionName = "Lnbits";

    public string LndhubUrl { get; set; } = string.Empty;
    public string WalletId { get; set; } = string.Empty;
    /// <summary>
    /// API-Key
    /// </summary>
    /// <remarks>
    /// Can be either Invoice-Key or Admin-Key depending on required permissions.
    /// </remarks>
    public string ApiKey { get; set; } = string.Empty;
}
