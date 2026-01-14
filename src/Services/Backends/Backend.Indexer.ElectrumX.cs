using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using TeamZaps.Configuration;
using TeamZaps.Utils;
using System.Diagnostics.CodeAnalysis;
using Microsoft.VisualBasic;
using System.Diagnostics;
using System.Net.Security;

namespace TeamZaps.Backend;


 public class ElectrumXClient : IBackendClient, IDisposable
 {
    #region Constants
    public const int TcpPort = 50001;
    public const int SslPort = 50002;
    
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(30);
    #endregion


    internal ElectrumXClient(ILogger logger, string host, ElectrumXSettings settings)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Failed to create client for unspecified host", nameof(host));

        this.logger = logger;
        this.ValidateCertificate = settings.ValidateSslCertificate;
        this.Timeout = settings.Timeout;

        var parts = host.Split(':', 2);
        this.Hostname = parts[0].Trim();
        if ((parts.Length > 1) && (int.TryParse(parts[1].Trim(), out var p)))
           this.Port = p;
        else
           this.Port = (settings.Port ?? TcpPort);
        
        // Determine SSL usage based on port:
        this.UseSsl = (this.Port == SslPort);
    }


    #region Properties.Management
    private TcpClient Client { get; } = new();
    private Stream Stream
    {
        get
        {
            if (!UseSsl)
                return (Client.GetStream());
            else if (sslStream is null)
                throw new InvalidOperationException("SSL stream not initialized");
            else
                return (sslStream);
        }
    }
    private SslStream? sslStream = null;

	public long SentRequests { get; private set; }
	public long FailedRequests { get; private set; }

    public bool Connected => Client.Connected;
    /// <summary>
    /// Gets whether the last read time exceeds the stale threshold.
    /// </summary>
    public bool IsStale => (lastRead is not null) && ((DateTime.UtcNow - lastRead!) < StaleThreshold);
    private DateTime? lastRead = null;
    #endregion
    #region Properties
    public string Hostname { get; }
    public int Port { get; }
    public bool UseSsl { get; }
    public bool ValidateCertificate { get; }
    /// <summary>
    /// Timeout in milliseconds.
    /// </summary>
    public int Timeout { get; set; }
    #endregion


    #region Connection
    public void Dispose()
    {
        sslStream?.Dispose();
        Client.Dispose();
    }
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (Connected)
            return;

        using var cts = new CancellationTokenSource(Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
        try
        {
            logger.LogDebug(new EventId(72, nameof(EnsureConnectedAsync)), "Connecting to ElectrumX server {Host}.", this);
            await Client.ConnectAsync(Hostname, Port, linkedCts.Token).ConfigureAwait(false);
            
            // Setup SSL/TLS if enabled
            if (UseSsl)
            {
                var networkStream = Client.GetStream();
                sslStream = new SslStream(
                    networkStream, 
                    false,
                    ValidateCertificate 
                        ? null 
                        : (sender, certificate, chain, errors) => true // Accept all certificates
                );
                var authOptions = new SslClientAuthenticationOptions { TargetHost = Hostname };
                await sslStream.AuthenticateAsClientAsync(authOptions, linkedCts.Token).ConfigureAwait(false);
                logger.LogDebug("SSL/TLS handshake completed for {Host}", this);
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to connect to ElectrumX server", ex);
        }
        if (Stream is null)
            throw new Exception("Not connected to ElectrumX server");
    }
    #endregion
    #region Communication
    public async Task<JsonNode> SendRequestAsync(string method, JsonArray? parameters = null, CancellationToken cancellationToken = default)
    {
        try
        {
            this.lastRead = DateTime.UtcNow;

            // Connect if not connected:
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

            // Create request:
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId++,
                ["method"] = method,
                ["params"] = (parameters ?? new JsonArray())
            };
            var requestJson = request.ToJsonString() + "\n";
            var requestBytes = Encoding.UTF8.GetBytes(requestJson);

            // Send request:
            logger.LogDebug(new EventId(90, nameof(SendRequestAsync)), "Sending '{Method}' request to {Host}.", method, this);
            await Stream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Read response
            using var cts = new CancellationTokenSource(Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
            {
                var buffer = new byte[8192];
                var responseBuilder = new StringBuilder();
                while (true)
                {
                    var bytesRead = await Stream.ReadAsync(buffer, linkedCts.Token).ConfigureAwait(false);
                    if (bytesRead == 0)
                        throw new IOException("Connection closed by server");

                    var chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    responseBuilder.Append(chunk);

                    // ElectrumX responses are newline-delimited
                    var response = responseBuilder.ToString();
                    if (response.Contains('\n'))
                    {
                        // Get the first complete line
                        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 0)
                        {
                            var rspJson = JsonNode.Parse(lines[0]);
                            if (rspJson?["error"] is JsonNode error)
                                throw new Exception($"ElectrumX error: {error?["message"]?.GetValue<string>() ?? "Unknown error"}");
                            if (rspJson?["result"] is JsonNode result)
                            {
                                SentRequests++;
                                return (result);
                            }
                            
                            throw new Exception("No response from ElectrumX server");
                        }
                    }
                }
            }
        }
        catch
        {
            FailedRequests++;
            throw;
        }
    }

    public Task PingAsync(CancellationToken cancellationToken = default) => SendRequestAsync("server.ping", null, cancellationToken);
    public async Task<ServerFeatures> GetServerFeaturesAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendRequestAsync("server.features", null, cancellationToken);
        var features = result.Deserialize<ServerFeatures>();
        if (features is null)
            throw new Exception("Failed to deserialize server features");
        else
            return features;
    }
    #endregion


    public override string ToString() => $"{Hostname}:{Port}";

    
    private readonly ILogger logger;
    private int requestId = 0;
}

/// <summary>
/// Backend service for connecting to ElectrumX servers to get blockchain information.
/// </summary>
[BackendDescription("ElectrumX")]
public class ElectrumXService : BackgroundService, IIndexerBackend, IMultiConnectionBackend, IDisposable
{
    #region Constants
    private const int RecommendedConnections = 3;
    private static readonly int RequestRetries = 1;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    #endregion


    public ElectrumXService(ILogger<ElectrumXService> logger) : this(logger, null) { }
    public ElectrumXService(ILogger<ElectrumXService> logger, IOptions<ElectrumXSettings>? settings)
    {
        this.logger = logger;
        this.settings = (settings?.Value ?? ElectrumXSettings.Default);
        this.rotatingHosts = GetConfiguredHosts().ToList();
        this.Hosts = rotatingHosts.AsReadOnly();

        if (Hosts.IsEmpty())
            throw new InvalidOperationException("No ElectrumX hosts configured!");
        else if (Hosts.Count == 1)
            logger.LogInformation("ElectrumX initialized with '{Host}'", Hosts.First());
        else
            logger.LogInformation("ElectrumX initialized with {Count} hosts", Hosts.Count);
    }


    #region Properties.Management
    IReadOnlyCollection<IBackendClient> IMultiConnectionBackend.Hosts => this.Hosts;
    public IReadOnlyCollection<ElectrumXClient> Hosts { get; }
    private List<ElectrumXClient> rotatingHosts;
    #endregion
    #region Properties
	public long SentRequests => Hosts.Sum(h => h.SentRequests);
	public long FailedRequests => Hosts.Sum(h => h.FailedRequests);
    public BlockHeader? LastBlock { get; private set; }
    #endregion


    #region Initialization
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Test configured connections:
        var requests = Hosts.Select(async host =>
        {
            try
            {
                var features = await host.GetServerFeaturesAsync(stoppingToken).ConfigureAwait(false);
                logger.LogDebug("Established connection to ElectrumX server '{Host}' ({Features})", host, features);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get features from ElectrumX server {Host}!", host);
            }
        });
        await Task.WhenAll(requests).ConfigureAwait(false);
        
        // Review server configuration:
        var succeededHosts = Hosts.Sum(h => h.SentRequests);
        var failedHosts = Hosts.Sum(h => h.FailedRequests);
        if (succeededHosts == 0)
            throw new InvalidOperationException("Failed to connect to any ElectrumX server! Unable to operate.");
        if (failedHosts == 0)
            logger.LogInformation("Successfully established connection to all ElectrumX servers.");
        else
            logger.LogWarning("Connection established to {Succeeded} of {Total} ElectrumX servers.", succeededHosts, Hosts.Count);
        if (succeededHosts < RecommendedConnections)
            logger.LogWarning("Count of {Succeeded} successful connections is below the recommended minimum of {RecommendedConnections}.", succeededHosts, RecommendedConnections);
    }
    public override Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public new void Dispose()
    {
        Hosts.ForEach(h => h.Dispose());
        base.Dispose();
    }
    #endregion
    #region Communication
    private Task<T> SendRequestAsync<T>(string method, JsonArray? parameters, CancellationToken cancellationToken, string propertyName) => SendRequestAsync<T>("blockchain.headers.subscribe", null, cancellationToken, (res) => GetValue<T>(res, propertyName));
    /// <summary>
    /// Sends a request to the configured ElectrumX hosts. Stale hosts are are considered to be used.
    /// </summary>
    private async Task<T> SendRequestAsync<T>(string method, JsonArray? parameters, CancellationToken cancellationToken, Func<JsonNode, T> valueFactory)
    {
        var result = await TrySendRequestAsync(true, method, parameters, cancellationToken).ConfigureAwait(false);
        if (result is null)
            // This should never happen, as stale hosts are enabled
            throw new NotImplementedException("Internal error on sending request to ElectrumX hosts.");
        else
            // Create value from response:
            return (valueFactory(result));
    }
    /// <summary>
    /// Sends a request to the configured ElectrumX hosts. Stale hosts are only considered if no default value could be provided.
    /// </summary>
    private async Task<T> SendRequestAsync<T>(string method, JsonArray? parameters, CancellationToken cancellationToken, T? defaultValue, Func<JsonNode, T> valueFactory)
    {
        // Send block request:
        var enStale = (defaultValue is null);
        var result = await TrySendRequestAsync(enStale, method, parameters, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            // All hosts are stale, return default value:
            Debug.Assert(defaultValue is not null);
            return (defaultValue!);
        }
        else
            // Create value from response:
            return (valueFactory(result));
    }
    /// <summary>
    /// Tries to send a request to the configured ElectrumX hosts, rotating through them until one succeeds or all fail.
    /// </summary>
    /// <param name="enableStale">If <c>true</c>, stale hosts will also be considered.</param>
    /// <returns>Returns the received response as a <see cref="JsonNode"/>, or <c>null</c> if all hosts are stale.</returns>
    /// <exception cref="AggregateException"></exception>
    private async Task<JsonNode?> TrySendRequestAsync(bool enableStale, string method, JsonArray? parameters = null, CancellationToken cancellationToken = default)
    {
        var failures = 0;
        var timeout = Stopwatch.StartNew();
        var maxRequests = (Hosts.Count * (RequestRetries + 1));
        for (int i = 0; i < maxRequests; i++)
        {
            if ((!TryGetRotatedHost(out var host)) && (!enableStale))
            {
                logger.LogDebug("Abort sending request hence all hosts are stale.");
                break;
            }
            if ((i >= Hosts.Count) && (timeout.Elapsed > RequestTimeout))
                throw new Exception($"Request '{method}' timed out after {failures} failure(s)! Refer to debug log for details.");

            try
            {
                var result = await host.SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
                if (result is null)
                    throw new Exception("No response from ElectrumX server");
                else
                {
                    if (failures > 0)
                        logger.LogWarning("Request '{Method}' succeeded after {Count} previous failure(s).", method, failures);

                    return (result);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Request '{Method}' failed!", method);
                failures++;
            }
        }

        if (failures == 0)
            return (null); // All hosts are stale at the moment.
        else
            throw new Exception($"Request '{method}' aborted after {failures} failure(s)! Refer to debug log for details.");
    }
    #endregion
    #region Operation
    /// <summary>
    /// Gets the current blockchain header information including block height and time.
    /// </summary>
    public async Task<BlockHeader> GetCurrentBlockAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await SendRequestAsync("blockchain.headers.subscribe", null, cancellationToken, LastBlock, (result) =>
            {
                BlockHeader currentBlock;
                if (result.TryDeserialize<BlockResponse.Layout1>(out var layout1))
                {
                    var headerBytes = Convert.FromHexString(layout1.Hex);
                    currentBlock = new BlockHeader {
                        Height = layout1.Height,
                        Hash = ComputeBlockHash(headerBytes),
                        BlockTime = ParseBlockTime(headerBytes)
                    };
                }
                else if (result.TryDeserialize<BlockResponse.Layout2>(out var layout2))
                {
                    var hexHeader = ConstructHexHeader(layout2);
                    var headerBytes = Convert.FromHexString(hexHeader);
                    currentBlock = new BlockHeader {
                        Height = layout2.BlockHeight,
                        Hash = ComputeBlockHash(headerBytes),
                        BlockTime = DateTimeOffset.FromUnixTimeSeconds(layout2.Timestamp)
                    };
                }
                else
                    throw new NotImplementedException("Unknown block response layout! Please remove host from server configuration.")
                        .AddLogLevel(LogLevel.Critical);

                if (LastBlock?.Height == currentBlock.Height)
                    logger.LogInformation("Keep using last block {Height}", currentBlock.Height);
                else
                    logger.LogInformation("Received new block {Height}", currentBlock.Height);

                return (this.LastBlock = currentBlock);
            }).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to get current block!", ex);
        }
    }
    public async Task<string> GetBlockAsync(int height, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await SendRequestAsync<string>("blockchain.headers.subscribe", null, cancellationToken, "hex").ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to get current block {height}!", ex);
        }
    }
    #endregion


    #region Helpers
    private IEnumerable<ElectrumXClient> GetConfiguredHosts()
    {
        if (!string.IsNullOrWhiteSpace(settings.Host))
            yield return new ElectrumXClient(logger, settings.Host, settings);
        if (settings.Hosts is not null)
        {
            foreach (var hostEntry in settings.Hosts)
            {
                if (!string.IsNullOrWhiteSpace(hostEntry))
                    yield return new ElectrumXClient(logger, hostEntry, settings);
            }
        }
    }
    /// <summary>
    /// Tries to return the next host that is not stale.
    /// </summary>
    /// <returns>Gets the next host, but will return <c>true</c> if only a non-stale host could be found.</returns>
    private bool TryGetRotatedHost(out ElectrumXClient host)
    {
        if (rotatingHosts.IsEmpty())
            throw new InvalidOperationException("No hosts configured!");

        // Get next rotated host that is not stale:
        lock (rotatingHosts)
        {
            for (int i = 0; i < rotatingHosts.Count; i++)
            {
                host = rotatingHosts.First();
                if (host.IsStale)
                    rotatingHosts.Rotate();
                else
                    return (true);
            }
        }
        host = rotatingHosts.First();
        return (false);
    }

    /// <summary>
    /// Constructs a hex-encoded block header from individual fields.
    /// </summary>
    /// <remarks>
    /// Block header format (80 bytes):
    /// - Version: 4 bytes (little-endian)
    /// - Previous block hash: 32 bytes (reversed for display)
    /// - Merkle root: 32 bytes (reversed for display)
    /// - Timestamp: 4 bytes (Unix timestamp, little-endian)
    /// - Bits: 4 bytes (little-endian)
    /// - Nonce: 4 bytes (little-endian)
    /// </remarks>
    private static string ConstructHexHeader(BlockResponse.Layout2 layout)
    {
        var header = new byte[80];
        var offset = 0;
        
        // Version (4 bytes, little-endian)
        BitConverter.GetBytes(layout.Version!.Value).CopyTo(header, offset);
        offset += 4;
        
        // Previous block hash (32 bytes, needs to be reversed from display format)
        var prevHashBytes = Convert.FromHexString(layout.PrevBlockHash!);
        Array.Reverse(prevHashBytes);
        prevHashBytes.CopyTo(header, offset);
        offset += 32;
        
        // Merkle root (32 bytes, needs to be reversed from display format)
        var merkleBytes = Convert.FromHexString(layout.MerkleRoot!);
        Array.Reverse(merkleBytes);
        merkleBytes.CopyTo(header, offset);
        offset += 32;
        
        // Timestamp (4 bytes, little-endian)
        BitConverter.GetBytes(layout.Timestamp).CopyTo(header, offset);
        offset += 4;
        
        // Bits (4 bytes, little-endian)
        BitConverter.GetBytes(layout.Bits!.Value).CopyTo(header, offset);
        offset += 4;
        
        // Nonce (4 bytes, little-endian)
        BitConverter.GetBytes(layout.Nonce!.Value).CopyTo(header, offset);
        
        return Convert.ToHexString(header).ToLowerInvariant();
    }
    
    /// <summary>
    /// Parses the block time from a raw block header.
    /// </summary>
    /// <remarks>
    /// Block header format (80 bytes):
    /// - Version: 4 bytes
    /// - Previous block hash: 32 bytes
    /// - Merkle root: 32 bytes
    /// - Timestamp: 4 bytes (Unix timestamp, little-endian)
    /// - Bits: 4 bytes
    /// - Nonce: 4 bytes
    /// </remarks>
    private static DateTimeOffset ParseBlockTime(byte[] headerBytes)
    {
        if (headerBytes.Length < 80)
            throw new ArgumentException("Invalid block header length", nameof(headerBytes));

        // Timestamp is at bytes 68-71 (little-endian)
        var timestampBytes = headerBytes[68..72];
        var timestamp = BitConverter.ToUInt32(timestampBytes, 0);

        return (DateTimeOffset.FromUnixTimeSeconds(timestamp));
    }
    private static string ComputeBlockHash(byte[] headerBytes)
    {
        if (headerBytes.Length < 80)
            throw new ArgumentException("Invalid block header length", nameof(headerBytes));

        var first = SHA256.HashData(headerBytes);
        var second = SHA256.HashData(first);

        // Bitcoin block hash is displayed as reversed byte order
        Array.Reverse(second);

        return (Convert.ToHexString(second).ToLowerInvariant());
    }

    /// <param name="propertyName">Name of the property to retrieve. Specify more than one if alternatives are possible.</param>
    private static T GetValue<T>(JsonNode node, params string[] propertyName)
    {
        foreach (var prop in propertyName)
        {
            var value = node[prop];
            if (value is not null)
            {
                Debug.WriteLine($"PROP={prop}");
                return (value.GetValue<T>());
            }
        }
        throw new Exception($"Missing '{propertyName.First()}' in response!");
    }
    #endregion


    #region Fields
    private readonly ILogger<ElectrumXService> logger;
    private readonly ElectrumXSettings settings;
    #endregion
}

public class ServerFeatures
{
    public class ServerHostInfo
    {
        [JsonPropertyName("ssl_port")]
        public int? SslPort { get; set; }
        [JsonPropertyName("tcp_port")]
        public int? TcpPort { get; set; }
        [JsonPropertyName("ws_port")]
        public int? WsPort { get; set; }
        [JsonPropertyName("wss_port")]
        public int? WssPort { get; set; }
    }


    [JsonPropertyName("dsproof")]
    public bool? Dsproof { get; set; }
    [JsonPropertyName("genesis_hash")]
    public string? GenesisHash { get; set; }
    [JsonPropertyName("hash_function")]
    public string? HashFunction { get; set; }
    [JsonPropertyName("hosts")]
    public Dictionary<string, ServerHostInfo>? Hosts { get; set; }
    [JsonPropertyName("protocol_max")]
    public string? ProtocolMax { get; set; }
    [JsonPropertyName("protocol_min")]
    public string? ProtocolMin { get; set; }
    [JsonPropertyName("pruning")]
    public object? Pruning { get; set; }
    [JsonPropertyName("server_version")]
    public string? ServerVersion { get; set; }


    public override string ToString() => $"{ServerVersion}, protocol {ProtocolMin}-{ProtocolMax}";
}

/// <summary>
/// Block response layouts.
/// </summary>
internal class BlockResponse
{
    public record Layout1
    {
        [JsonPropertyName("hex")]
        public required string Hex { get; init; }
        [JsonPropertyName("height")]
        public required int Height { get; init; }
    }
    public record Layout2
    {
        [JsonPropertyName("version")]
        public uint? Version { get; init; }
        [JsonPropertyName("prev_block_hash")]
        public string? PrevBlockHash { get; init; }
        [JsonPropertyName("merkle_root")]
        public required string MerkleRoot { get; init; }
        [JsonPropertyName("timestamp")]
        public required uint Timestamp { get; init; }
        [JsonPropertyName("bits")]
        public uint? Bits { get; init; }
        [JsonPropertyName("nonce")]
        public uint? Nonce { get; init; }
        
        [JsonPropertyName("block_height")]
        public required int BlockHeight { get; init; }
    }
}