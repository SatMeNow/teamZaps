using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using teamZaps.Configuration;
using teamZaps.Utils;
using System.Diagnostics.CodeAnalysis;

namespace teamZaps.Backend;


/// <summary>
/// Backend service for connecting to ElectrumX servers to get blockchain information.
/// </summary>
[BackendDescription("ElectrumX")]
public class ElectrumXService : IIndexerBackend, IDisposable
{
    public ElectrumXService(ILogger<ElectrumXService> logger, IOptions<ElectrumXSettings> settings)
    {
        this.logger = logger;
        if (string.IsNullOrEmpty(settings.Value.Host))
            this.settings = ElectrumXSettings.Default;
        else
            this.settings = settings.Value;

        logger.LogInformation("ElectrumX initialized for {Host}:{Port}", this.settings.Host, this.settings.Port);
    }


    #region Properties
	public ulong SentRequests { get; private set; }
    public IBlockHeader? LastBlock { get; private set; }
    #endregion


    #region Initialization
    public void Dispose()
    {
        stream?.Dispose();
        client?.Dispose();
    }
    #endregion
    #region Connection
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (client?.Connected == true)
            return;

        client?.Dispose();
        client = new TcpClient();
        
        using var cts = new CancellationTokenSource(settings.TimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
        
        await client.ConnectAsync(settings.Host, settings.Port, linkedCts.Token).ConfigureAwait(false);
        stream = client.GetStream();

        logger.LogInformation("Connected to ElectrumX server");
    }
    #endregion
    #region RPC
    private async Task<JsonNode?> SendRequestAsync(string method, JsonArray? parameters = null, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        if (stream is null)
            throw new InvalidOperationException("Not connected to ElectrumX server");

        requestId++;
        SentRequests++;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
            ["method"] = method,
            ["params"] = parameters ?? new JsonArray()
        };

        var requestJson = request.ToJsonString() + "\n";
        var requestBytes = Encoding.UTF8.GetBytes(requestJson);

        await stream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Read response
        var buffer = new byte[8192];
        var responseBuilder = new StringBuilder();
        
        using var cts = new CancellationTokenSource(settings.TimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, linkedCts.Token).ConfigureAwait(false);
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
                    var responseJson = JsonNode.Parse(lines[0]);
                    
                    if (responseJson?["error"] is not null)
                    {
                        var error = responseJson["error"];
                        throw new Exception($"ElectrumX error: {error?["message"]?.GetValue<string>() ?? "Unknown error"}");
                    }

                    return (responseJson?["result"]);
                }
            }
        }
    }
    #endregion
    #region Operation
    /// <summary>
    /// Gets the current blockchain header information including block height and time.
    /// </summary>
    public async Task<IBlockHeader> GetCurrentBlockAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Send block request
            var result = await UtilTask.DelayWhileAsync(() => SendRequestAsync("blockchain.headers.subscribe", null, cancellationToken), 3000, 500, cancellationToken).ConfigureAwait(false);
            if (result is null)
                throw new Exception("No response from ElectrumX server");

            var height = result["height"]?.GetValue<int>() ?? throw new Exception("Missing height in response");
            var hexHeader = result["hex"]?.GetValue<string>() ?? throw new Exception("Missing hex in response");

            // Parse the block header (80 bytes)
            var headerBytes = Convert.FromHexString(hexHeader);
            var blockTime = ParseBlockTime(headerBytes);

            if (LastBlock?.Height != height)
                logger.LogInformation("Current block: height={Height}, time={BlockTime}", height, blockTime);

            return (this.LastBlock = new BlockHeader
            {
                Height = height,
                HexHeader = hexHeader,
                Hash = ComputeBlockHash(headerBytes),
                BlockTime = blockTime,
                LocalTime = blockTime.ToLocalTime()
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get current block header from ElectrumX");
            throw;
        }
    }
    public async Task<string> GetBlockHeaderAsync(int height, CancellationToken cancellationToken = default)
    {
        try
        {
            var parameters = new JsonArray { height };
            var result = await SendRequestAsync("blockchain.block.header", parameters, cancellationToken).ConfigureAwait(false);
            
            return (result?.GetValue<string>() ?? throw new Exception("No header returned"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get block header at height {Height}", height);
            throw;
        }
    }
    public Task PingAsync(CancellationToken cancellationToken = default) => SendRequestAsync("server.ping", null, cancellationToken);
    public Task<JsonNode?> GetServerFeaturesAsync(CancellationToken cancellationToken = default) => SendRequestAsync("server.features", null, cancellationToken);
    #endregion


    #region Helpers
    /// <summary>
    /// Parses the block time from a raw block header.
    /// Block header format (80 bytes):
    /// - Version: 4 bytes
    /// - Previous block hash: 32 bytes
    /// - Merkle root: 32 bytes
    /// - Timestamp: 4 bytes (Unix timestamp, little-endian)
    /// - Bits: 4 bytes
    /// - Nonce: 4 bytes
    /// </summary>
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
    private TcpClient? client;
    private NetworkStream? stream;
    private int requestId = 0;
    #endregion
}

file record BlockHeader : IBlockHeader
{
    public int Height { init; get; }
    public string HexHeader { init; get; } = string.Empty;
    public string Hash { init; get; } = string.Empty;
    public DateTimeOffset BlockTime { init; get; }
    public DateTimeOffset LocalTime { init; get; }


    public override string ToString() => $"{Height} ({BlockTime:f})";
}
