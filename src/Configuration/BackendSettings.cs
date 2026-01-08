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
        Timeout = 10000
    };
    #endregion


    /// <inheritdoc cref="Host"/>
    /// <summary>
    /// Array of hosts in <c>HOST:PORT</c> notation.
    /// </summary>
    public string[]? Hosts { get; set; }
    /// <summary>
    /// Hosts in <c>HOST:PORT</c> notation. Port is optional and defaults to <see cref="Port"/>.
    /// </summary>
    /// <remarks>
    /// Port is optional and defaults to <see cref="Port"/>.
    /// </remarks>
    public string? Host { get; set; }
    /// <remarks>
    /// 50001 as TCP port, 50002 for SSL.
    /// </remarks>
    public int? Port { get; set; }
    public bool UseSsl { get; set; }
    /// <summary>
    /// Timeout in milliseconds.
    /// </summary>
    public int Timeout { get; set; }
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
