using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using teamZaps.Configuration;
using teamZaps.Utils;
using System.Diagnostics.CodeAnalysis;
using Microsoft.VisualBasic;
using System.Diagnostics;

namespace teamZaps.Backend;


 public class ElectrumXClient : IBackendClient, IDisposable
 {
    #region Constants
    private const int DefaultPort = 50001;
    
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(30);
    #endregion


    internal ElectrumXClient(ILogger logger, string host, int? port, int timeout)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Failed to create client for unspecified host", nameof(host));

        this.logger = logger;
        this.Timeout = timeout;

        var parts = host.Split(':', 2);
        this.Hostname = parts[0].Trim();
        if ((parts.Length > 1) && (int.TryParse(parts[1].Trim(), out var p)))
           this.Port = p;
        else
           this.Port = (port ?? DefaultPort);
    }


    #region Properties.Management
    private TcpClient Client { get; } = new();
    private NetworkStream Stream => Client.GetStream();

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
    /// <summary>
    /// Timeout in milliseconds.
    /// </summary>
    public int Timeout { get; set; }
    #endregion


    #region Connection
    public void Dispose()
    {
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
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to connect to ElectrumX server", ex);
        }
        if (Stream is null)
            throw new Exception("Not connected to ElectrumX server");
        else
            logger.LogInformation("Connected to ElectrumX server");
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
    public Task<JsonNode> SendRequestAsync(string method, JsonArray? parameters = null, CancellationToken cancellationToken = default) => GetRotatedHost()
        .SendRequestAsync(method, parameters, cancellationToken);
    /// <summary>
    /// Tries to send a request to the configured ElectrumX hosts, rotating through them until one succeeds or all fail.
    /// </summary>
    /// <returns>Returns the received response as a <see cref="JsonNode"/>, or <c>null</c> if all hosts are stale.</returns>
    /// <exception cref="AggregateException"></exception>
    public async Task<JsonNode?> TrySendRequestAsync(string method, JsonArray? parameters = null, CancellationToken cancellationToken = default)
    {
        var failures = 0;
        for (int i = 0; i < Hosts.Count; i++)
        {
            if (!TryGetRotatedHost(out var host))
                break; // Hosts are stale at the moment.

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
            // Send block request
            var result = await TrySendRequestAsync("blockchain.headers.subscribe", null, cancellationToken).ConfigureAwait(false);
            if (result is null)
                return (LastBlock!); // Last block is still fresh.

            var height = result["height"]?.GetValue<int>() ?? throw new Exception("Missing height in response");
            var hexHeader = result["hex"]?.GetValue<string>() ?? throw new Exception("Missing hex in response");

            // Parse the block header (80 bytes)
            var headerBytes = Convert.FromHexString(hexHeader);
            var blockTime = ParseBlockTime(headerBytes);

            if (LastBlock?.Height != height)
                logger.LogInformation("Current block: height={Height}, time={BlockTime}", height, blockTime);

            return (this.LastBlock = new ElectrumBlockHeader
            {
                Height = height,
                HexHeader = hexHeader,
                Hash = ComputeBlockHash(headerBytes),
                BlockTime = blockTime
            });
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to get current block", ex);
        }
    }
    public async Task<string> GetBlockAsync(int height, CancellationToken cancellationToken = default)
    {
        try
        {
            var parameters = new JsonArray { height };
            var result = await SendRequestAsync("blockchain.block.header", parameters, cancellationToken).ConfigureAwait(false);
            
            return (result.GetValue<string>());
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to get current block {height}", ex);
        }
    }
    #endregion


    #region Helpers
    private IEnumerable<ElectrumXClient> GetConfiguredHosts()
    {
        if (!string.IsNullOrWhiteSpace(settings.Host))
            yield return new ElectrumXClient(logger, settings.Host, settings.Port, settings.Timeout);
        if (settings.Hosts is not null)
        {
            foreach (var hostEntry in settings.Hosts)
            {
                if (!string.IsNullOrWhiteSpace(hostEntry))
                    yield return new ElectrumXClient(logger, hostEntry, settings.Port, settings.Timeout);
            }
        }
    }
    /// <summary>
    /// Returns the next host that is not stale.
    /// </summary>
    /// <returns>If all hosts are stale, the next stale host is returned.</returns>
    private ElectrumXClient GetRotatedHost()
    {
        TryGetRotatedHost(out var host);
        return (host);
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
    #endregion


    #region Fields
    private readonly ILogger<ElectrumXService> logger;
    private readonly ElectrumXSettings settings;
    #endregion
}

file class ElectrumBlockHeader : BlockHeader
{
    [JsonIgnore]
    public string HexHeader { get; set; } = string.Empty;
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
