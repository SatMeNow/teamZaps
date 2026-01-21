namespace TeamZaps.Configuration;

public class ElectrumXSettings
{
    #region Constants
    public const string SectionName = "ElectrumX";
    public static ElectrumXSettings Default => new()
    {
        Hosts = [ "electrum.blockstream.info" ],
        Port = 50001,
        ValidateSslCertificate = true,
        Timeout = 10000
    };
    #endregion


    /// <inheritdoc cref="Host"/>
    /// <summary>
    /// Array of hosts in <c>HOST:PORT</c> notation.
    /// </summary>
    /// <remarks>
    /// Port is optional and defaults to <see cref="Port"/>.
    /// <remarks>
    public string[]? Hosts { get; set; }
    /// <remarks>
    /// 50001 as TCP port, 50002 for SSL.
    /// </remarks>
    public int? Port { get; set; }
    /// <summary>
    /// Validate SSL/TLS certificates. Set to false to accept self-signed certificates.
    /// </summary>
    /// <remarks>
    /// When false, all certificates are accepted (less secure). Use with caution.
    /// </remarks>
    public bool ValidateSslCertificate { get; set; }
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
