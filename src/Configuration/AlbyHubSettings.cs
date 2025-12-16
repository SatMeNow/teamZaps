namespace teamZaps.Configuration;

public class AlbyHubSettings
{
    public const string SectionName = "AlbyHub";

    /// <summary>
    /// Nostr Wallet Connect connection string (nostr+walletconnect://...)
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
    /// <summary>
    /// Relay URLs for Nostr communication (optional, overrides connection string relays)
    /// </summary>
    public string[]? RelayUrls { get; set; }
}
