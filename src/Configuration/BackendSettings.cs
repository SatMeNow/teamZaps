namespace teamZaps.Configuration;

public class ElectrumXSettings
{
    #region Constants
    public const string SectionName = "ElectrumX";
    public static ElectrumXSettings Default => new()
    {
        Host = "electrum.blockstream.info",
        Port = 50001,
        UseSsl = false,
        TimeoutMs = 10000
    };
    #endregion


    public required string Host { get; set; }
    /// <remarks>
    /// 50001 as TCP port, 50002 for SSL.
    /// </remarks>
    public required int Port { get; set; }
    public bool UseSsl { get; set; }
    public int TimeoutMs { get; set; }
}

public class LnbitsSettings
{
    #region Constants
    public const string SectionName = "Lnbits";
    #endregion


    public string LndhubUrl { get; set; } = string.Empty;
    /// <summary>
    /// API-Key
    /// </summary>
    /// <remarks>
    /// Can be either Invoice-Key or Admin-Key depending on required permissions.
    /// </remarks>
    public string ApiKey { get; set; } = string.Empty;
}

public class AlbyHubSettings
{
    #region Constants
    public const string SectionName = "AlbyHub";
    #endregion


    /// <summary>
    /// Nostr Wallet Connect connection string (nostr+walletconnect://...)
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
    /// <summary>
    /// Relay URLs for Nostr communication (optional, overrides connection string relays)
    /// </summary>
    public string[]? RelayUrls { get; set; }
}

public class TelegramSettings
{
    #region Constants
    public const string SectionName = "Telegram";
    #endregion

    
    public string BotToken { get; set; } = string.Empty;
    /// <summary>
    /// List of root user IDs who have elevated permissions.
    /// </summary>
    public long[] RootUsers { get; set; } = [];
}
