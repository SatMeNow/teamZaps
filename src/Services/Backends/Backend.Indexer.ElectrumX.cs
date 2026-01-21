using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using TeamZaps.Configuration;
using TeamZaps.Utils;
using TeamZaps.Services;
using TeamZaps.Session;
using System.Diagnostics;
using System.Net.Security;

namespace TeamZaps.Backends.Indexer;


 public class ElectrumXClient : IBackendClient, IDisposable
 {
    #region Constants
    public const int TcpPort = 50001;
    public const int SslPort = 50002;
    
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(60);
    #endregion


    internal ElectrumXClient(ILogger logger, string host, ElectrumXSettings settings)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Failed to create client for unspecified host!", nameof(host));

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
    private TcpClient Client { set; get; } = null!;
    private Stream Stream
    {
        get
        {
            if (!UseSsl)
                return (Client.GetStream());
            else if (sslStream is null)
                throw new InvalidOperationException("SSL stream not initialized!");
            else
                return (sslStream);
        }
    }
    private SslStream? sslStream = null;

	public long SentRequests { get; private set; }
	public long FailedRequests { get; private set; }

    public bool Connected => (Client?.Connected ?? false);
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
    
    
    #region Delegates
    /// <summary>
    /// Event handler for subscription notifications
    /// </summary>
    public delegate void NotificationHandler(string method, JsonArray? parameters);
    #endregion


    #region Connection
    public void Dispose() => Disconnect();
    private void Disconnect()
    {
        logger.LogDebug("Disconnecting from {Host}.", this);
        try
        {
            // Cancel keepalive task
            keepaliveCts?.Cancel();
            keepaliveTask?.Wait(TimeSpan.FromSeconds(2));
            keepaliveCts?.Dispose();
            keepaliveCts = null;
            keepaliveTask = null;
            
            // Cancel background listener
            listenerCts?.Cancel();
            listenerTask?.Wait(TimeSpan.FromSeconds(2));
            listenerCts?.Dispose();
            listenerCts = null;
            listenerTask = null;
            
            // Clear pending requests
            lock (pendingRequests)
            {
                foreach (var pending in pendingRequests.Values)
                    pending.TrySetCanceled();
                pendingRequests.Clear();
            }
            
            messageBuffer.Clear();
            
            sslStream?.Dispose();
            sslStream = null;

            if (Client is not null)
            {
                Client.Close();
                Client.Dispose();
                Client = null!;
            }
        }
        catch
        {
            // Ignore errors during disconnect
        }
    }
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (Connected)
            return;

        // Create new TcpClient for this connection
        this.Client = new TcpClient();

        using var cts = new CancellationTokenSource(Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
        try
        {
            logger.LogDebug(new EventId(72, nameof(EnsureConnectedAsync)), "Connecting to ElectrumX server {Host}.", this);
            await Client.ConnectAsync(Hostname, Port, linkedCts.Token).ConfigureAwait(false);
            
            // Enable TCP keep-alive to detect dead connections
            Client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            // Configure keep-alive parameters (send probe after 30s idle, every 10s, 3 times)
            if (OperatingSystem.IsWindows())
            {
                // Windows-specific keep-alive settings
                var keepAliveValues = new byte[12];
                BitConverter.GetBytes(1u).CopyTo(keepAliveValues, 0);        // on/off
                BitConverter.GetBytes(30000u).CopyTo(keepAliveValues, 4);    // time (ms)
                BitConverter.GetBytes(10000u).CopyTo(keepAliveValues, 8);    // interval (ms)
                Client.Client.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);
            }
            
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
                logger.LogDebug("SSL/TLS handshake completed for {Host}.", this);
            }
            
            // Start background listener for subscription notifications
            StartBackgroundListener();
            
            // Start keepalive task
            StartKeepaliveTask();
        }
        catch (Exception ex)
        {
            Disconnect();
            throw new Exception($"Failed to connect to ElectrumX server.", ex);
        }
        if (Stream is null)
            throw new Exception("Not connected to ElectrumX server.");
    }
    #endregion
    #region Communication
    public async Task<JsonNode> SendRequestAsync(string method, JsonArray? parameters = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // Connect if not connected:
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            if (!Connected)
                throw new IOException("Connection not established.");

            // Create request:
            var currentRequestId = requestId++;
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = currentRequestId,
                ["method"] = method,
                ["params"] = (parameters ?? new JsonArray())
            };
            var requestJson = request.ToJsonString() + "\n";
            var requestBytes = Encoding.UTF8.GetBytes(requestJson);

            // Create TaskCompletionSource for this request
            var tcs = new TaskCompletionSource<JsonNode>();
            lock (pendingRequests)
            {
                pendingRequests[currentRequestId] = tcs;
            }

            try
            {
                // Send request:
                logger.LogTrace(new EventId(90, nameof(SendRequestAsync)), "Sending `{Method}` request (id={Id}) to {Host}.", method, currentRequestId, this);
                
                const int maxSendAttempts = 2; // Original attempt + 1 retry
                Exception? lastSendException = null;
                for (int sendAttempt = 0; sendAttempt < maxSendAttempts; sendAttempt++)
                {
                    try
                    {
                        // For SSL connections, verify once more before writing
                        if (UseSsl && sslStream is not null)
                        {
                            if (!sslStream.CanWrite)
                            {
                                logger.LogDebug("SSL stream became unwritable for {Host}, reconnecting...", this);
                                Disconnect();
                                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                            }
                        }
                        
                        await Stream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
                        await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        
                        // Success - break out of retry loop
                        lastSendException = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Check if this is a recoverable error
                        var isRecoverable = (
                            ex is IOException || 
                            ex.InnerException is SocketException ||
                            (ex is SocketException se && se.SocketErrorCode == SocketError.ConnectionAborted)
                        );
                        if (!isRecoverable)
                        {
                            logger.LogDebug(ex, "Non-recoverable error during send to {Host}.", this);
                            throw new IOException($"Failed to send request to server.", ex);
                        }
                        
                        lastSendException = ex;
                        logger.LogDebug(ex, "Recoverable error during write to {Host} (attempt {Attempt}/{Max}).", this, (sendAttempt + 1), maxSendAttempts);
                        
                        Disconnect();
                        
                        // If this wasn't the last attempt, reconnect and retry
                        if (sendAttempt < maxSendAttempts - 1)
                        {
                            logger.LogDebug("Attempting to reconnect to {Host} for retry...", this);
                            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                
                // If we still have an exception after all retries, throw it
                if (lastSendException is not null)
                    throw new IOException($"Failed to send request to server after {maxSendAttempts} attempts (stale connection).", lastSendException);

                // Wait for response from background listener
                using var cts = new CancellationTokenSource(Timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
                await using (linkedCts.Token.Register(() => tcs.TrySetCanceled()))
                {
                    var result = await tcs.Task.ConfigureAwait(false);
                    SentRequests++;
                    return result;
                }
            }
            finally
            {
                // Clean up pending request
                lock (pendingRequests)
                {
                    pendingRequests.Remove(currentRequestId);
                }
            }
        }
        catch (Exception ex)
        {
            FailedRequests++;

            // Disconnect on connection-related errors
            if (ex is IOException or TimeoutException or OperationCanceledException || 
                !Connected || 
                ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug(ex, "Disconnecting from {Host} due to error.", this);
                Disconnect();
            }
            
            // Re-throw with context
            throw ex switch
            {
                TimeoutException => new TimeoutException($"Request `{method}` timed out with '{this}'!", ex),
                IOException => new IOException($"IO error occurred during `{method}` with '{this}'!", ex),
                _ => new Exception($"Error occurred during `{method}` with '{this}'!", ex)
            };
        }
    }

    /// <summary>
    /// Subscribe to server notifications for a specific method
    /// </summary>
    public void Subscribe(string method, NotificationHandler handler)
    {
        lock (subscriptionHandlers)
        {
            if (!subscriptionHandlers.TryGetValue(method, out var handlers))
            {
                handlers = new List<NotificationHandler>();
                subscriptionHandlers[method] = handlers;
            }
            handlers.Add(handler);
        }
        logger.LogDebug("Subscribed to `{Method}` notifications from {Host}.", method, this);
    }
    /// <summary>
    /// Unsubscribe from server notifications
    /// </summary>
    public void Unsubscribe(string method, NotificationHandler handler)
    {
        lock (subscriptionHandlers)
        {
            if (subscriptionHandlers.TryGetValue(method, out var handlers))
            {
                handlers.Remove(handler);
                if (handlers.Count == 0)
                    subscriptionHandlers.Remove(method);
            }
        }
    }

    /// <summary>
    /// Background task that sends periodic pings to keep the connection alive
    /// </summary>
    private void StartKeepaliveTask()
    {
        if (keepaliveTask is not null && !keepaliveTask.IsCompleted)
            return; // Already running
            
        keepaliveCts = new CancellationTokenSource();
        keepaliveTask = Task.Run(async () => await KeepaliveLoop(keepaliveCts.Token).ConfigureAwait(false));
        logger.LogDebug("Keepalive task started for {Host}.", this);
    }
    private async Task KeepaliveLoop(CancellationToken cancellationToken)
    {
        try
        {
            // Send ping to keep connection alive
            while (!cancellationToken.IsCancellationRequested && Connected)
            {
                await Task.Delay(PingInterval, cancellationToken).ConfigureAwait(false);
                if (!Connected)
                    break;
                
                try
                {
                    logger.LogTrace("Sending keepalive ping to {Host}.", this);
                    await PingAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Keepalive ping failed for {Host}, connection may be dead.", this);
                    break;
                }
            }
        }
        finally
        {
            logger.LogDebug("Keepalive task stopped for {Host}.", this);
        }
    }
    
    /// <summary>
    /// Background task that continuously reads messages from the server
    /// </summary>
    private void StartBackgroundListener()
    {
        if (listenerTask is not null && !listenerTask.IsCompleted)
            return; // Already running
            
        listenerCts = new CancellationTokenSource();
        listenerTask = Task.Run(async () => await BackgroundListenerLoop(listenerCts.Token).ConfigureAwait(false));
        logger.LogDebug("Background listener started for {Host}.", this);
    }
    private async Task BackgroundListenerLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested && Connected)
            {
                try
                {
                    // Read data from stream
                    var bytesRead = await Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        logger.LogDebug("Connection closed by server {Host}.", this);
                        break;
                    }
                    var chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuffer.Append(chunk);

                    // Process all complete messages (newline-delimited)
                    while (true)
                    {
                        var content = messageBuffer.ToString();
                        var newlineIndex = content.IndexOf('\n');
                        if (newlineIndex < 0)
                            break; // No complete message yet

                        var line = content.Substring(0, newlineIndex).Trim();
                        messageBuffer.Clear();
                        messageBuffer.Append(content.Substring(newlineIndex + 1));
                        if (string.IsNullOrEmpty(line))
                            continue; // Empty line

                        // Parse and handle message
                        try
                        {
                            var message = JsonNode.Parse(line);
                            if (message is null)
                                continue;

                            // Check if this is a response
                            if (message["id"] is JsonNode idNode)
                            {
                                var id = idNode.GetValue<int>();
                                
                                TaskCompletionSource<JsonNode>? tcs = null;
                                lock (pendingRequests)
                                {
                                    if (pendingRequests.TryGetValue(id, out tcs))
                                        pendingRequests.Remove(id);
                                }

                                if (tcs is not null)
                                {
                                    if (message["error"] is JsonNode error)
                                    {
                                        var errorMsg = error["message"]?.GetValue<string>() ?? "Unknown error";
                                        tcs.TrySetException(new Exception($"ElectrumX error: {errorMsg}"));
                                    }
                                    else if (message.AsObject().ContainsKey("result"))
                                        // Result key exists (even if value is null)
                                        tcs.TrySetResult(message["result"]!);
                                    else
                                        tcs.TrySetException(new Exception("Invalid response: no result or error."));
                                }
                                else
                                    logger.LogTrace("Received response for unknown request id {Id} from {Host}.", id, this);
                            }
                            // Check if this is a subscription notification
                            else if (message["method"] is JsonNode methodNode)
                            {
                                var method = methodNode.GetValue<string>();
                                var parameters = message["params"]?.AsArray();
                                
                                logger.LogDebug("Received subscription notification: `{Method}` from {Host}.", method, this);
                                
                                // Dispatch to handlers
                                List<NotificationHandler>? handlers = null;
                                lock (subscriptionHandlers)
                                {
                                    if (subscriptionHandlers.TryGetValue(method, out var h))
                                        handlers = new List<NotificationHandler>(h); // Copy to avoid lock issues
                                }
                                
                                if (handlers is not null)
                                {
                                    foreach (var handler in handlers)
                                    {
                                        try
                                        {
                                            handler(method, parameters);
                                        }
                                        catch (Exception ex)
                                        {
                                            logger.LogError(ex, "Error in notification handler for `{Method}` from {Host}.", method, this);
                                        }
                                    }
                                }
                                else
                                    logger.LogDebug("No handlers registered for `{Method}` notification from {Host}.", method, this);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to parse message from {Host}: {Line}.", this, line);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in background listener for {Host}.", this);
                    break;
                }
            }
        }
        finally
        {
            logger.LogDebug("Background listener stopped for {Host}.", this);
        }
    }

    public async Task Connect(CancellationToken cancellationToken = default)
    {
        var features = await GetServerFeaturesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Connected to ElectrumX server '{Host}' ({Features}).", this, features);
    }

    public Task PingAsync(CancellationToken cancellationToken = default) => SendRequestAsync("server.ping", null, cancellationToken);
    public async Task<ServerFeatures> GetServerFeaturesAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendRequestAsync("server.features", null, cancellationToken);
        var features = result.Deserialize<ServerFeatures>();
        if (features is null)
            throw new Exception("Failed to deserialize server features.");
        else
            return features;
    }
    #endregion


    public override string ToString() => $"{Hostname}:{Port}";

    
    private readonly ILogger logger;
    private int requestId = 0;
    
    private readonly Dictionary<string, List<NotificationHandler>> subscriptionHandlers = new();
    /// <summary>
    /// Dictionary of pending requests awaiting responses
    /// </summary>
    private readonly Dictionary<int, TaskCompletionSource<JsonNode>> pendingRequests = new();
    
    private Task? listenerTask = null;
    private CancellationTokenSource? listenerCts = null;
    
    private Task? keepaliveTask = null;
    private CancellationTokenSource? keepaliveCts = null;
    
    /// <summary>
    /// Buffer for incomplete messages
    /// </summary>
    private readonly StringBuilder messageBuffer = new();
}

/// <summary>
/// Backend service for connecting to ElectrumX servers to get blockchain information.
/// </summary>
[BackendDescription("ElectrumX")]
public class ElectrumXService : BackgroundService, IIndexerBackend, IMultiConnectionBackend, IDisposable
{
    #region Constants
    private static readonly TimeSpan StaleBlockDataThreshold = TimeSpan.FromMinutes(60);
    #endregion


    public ElectrumXService(ILogger<ElectrumXService> logger) : this(logger, null) { }
    public ElectrumXService(ILogger<ElectrumXService> logger, IOptions<ElectrumXSettings>? settings)
    {
        this.logger = logger;
        this.settings = (settings?.Value ?? ElectrumXSettings.Default);
        this.ConfiguredClients = GetConfiguredHosts(this.settings.Hosts)
            .Select(h => new ElectrumXClient(logger, h, this.settings))
            .ToArray();

        if (ConfiguredClients.IsEmpty())
            throw new InvalidOperationException("No ElectrumX hosts configured.");
        else if (ConfiguredClients.Count == 1)
            logger.LogInformation("ElectrumX initialized with 1 host (no failover available).");
        else
            logger.LogInformation("ElectrumX initialized with {Count} hosts.", ConfiguredClients.Count);

        // Start with first configured client
        this.ActiveClient = ConfiguredClients.First();
    }


    #region Properties.Management
    IReadOnlyCollection<IBackendClient> IMultiConnectionBackend.Hosts => ConfiguredClients;
    public IReadOnlyCollection<ElectrumXClient> ConfiguredClients { get; }
    public ElectrumXClient ActiveClient { get; private set; }
    #endregion
    #region Properties
	public long SentRequests => ActiveClient?.SentRequests ?? 0;
	public long FailedRequests => ActiveClient?.FailedRequests ?? 0;
    public BlockHeader? LastBlock { get; private set; }
    public int ReceivedBlocks { get; private set; }
    #endregion


    #region Initialization
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Test connection to active host:
        try
        {
            await ActiveClient.Connect(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to connect to ElectrumX server {Host}!", this);

            // Try to failover to next host
            if (!await TryFailoverToNextHostAsync(stoppingToken).ConfigureAwait(false))
                throw new InvalidOperationException("Failed to connect to any configured ElectrumX server! Unable to operate.");
        }
        
        // Setup subscriptions for block headers
        await SetupSubscriptionsAsync(stoppingToken).ConfigureAwait(false);
        
        // Start reconnection monitor
        StartReconnectionMonitor(stoppingToken);
    }
    
    private async Task SetupSubscriptionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            lock (clientLock)
            {
                // Register notification handler
                ActiveClient.Subscribe("blockchain.headers.subscribe", (method, parameters) =>
                {
                    try
                    {
                        if (parameters is not null && parameters.Count > 0)
                        {
                            var blockData = parameters[0];
                            if (blockData is not null)
                                OnBlockHeaderNotification(blockData);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error handling block header notification from {Host}.", ActiveClient);
                    }
                });
            }
            
            // Send initial subscription request
            var initialBlock = await ActiveClient.SendRequestAsync("blockchain.headers.subscribe", null, cancellationToken).ConfigureAwait(false);
            OnBlockHeaderNotification(initialBlock);
            
            logger.LogInformation("Successfully subscribed to block headers from {Host}.", ActiveClient);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to subscribe to block headers from {Host}.", ActiveClient);
        }
    }
    
    private void StartReconnectionMonitor(CancellationToken stoppingToken)
    {
        reconnectionCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, reconnectionCts.Token);
        
        reconnectionTask = Task.Run(async () =>
        {
            while (!linkedCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), linkedCts.Token).ConfigureAwait(false);
                    
                    // Check if active client is still connected
                    ElectrumXClient client;
                    lock (clientLock)
                    {
                        client = ActiveClient;
                    }
                    
                    if (!client.Connected)
                    {
                        logger.LogWarning("Detected disconnection from {Host}, attempting failover...", client);
                        
                        // Connection is dead, go directly to next host:
                        await TryFailoverToNextHostAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in reconnection monitoring.");
                }
            }
            logger.LogDebug("Reconnection monitoring stopped.");
        }, linkedCts.Token);
        
        logger.LogDebug("Reconnection monitoring started.");
    }
    
    private void OnBlockHeaderNotification(JsonNode blockData)
    {
        try
        {
            var currentBlock = ParseBlockHeader(blockData);
            if (LastBlock?.Height != currentBlock.Height)
            {
                var logLevel = (ReceivedBlocks % 10 == 0) ? LogLevel.Information : LogLevel.Debug;
                logger.Log(logLevel, "Received 📦 block {Height} from subscription.", currentBlock.Height);

                LastBlock = currentBlock;
                ReceivedBlocks++;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing block header notification.");
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        reconnectionCts?.Cancel();
        if (reconnectionTask is not null)
        {
            await reconnectionTask.ConfigureAwait(false);
        }
        reconnectionCts?.Dispose();
    }
    public new void Dispose()
    {
        lock (clientLock)
        {
            ActiveClient?.Dispose();
        }
        base.Dispose();
    }
    #endregion
    #region Communication
    /// <summary>
    /// Sends a request to the active ElectrumX server with automatic failover on connection errors.
    /// </summary>
    private async Task<JsonNode> SendRequestAsync(string method, JsonArray? parameters, CancellationToken cancellationToken)
    {
        ElectrumXClient client;
        lock (clientLock)
        {
            client = ActiveClient;
        }
        
        try
        {
            return await client.SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Request `{Method}` failed on {Host}, attempting failover.", method, client);
            
            // Try to failover to next host
            if (await TryFailoverToNextHostAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (clientLock)
                {
                    client = ActiveClient;
                }
                return await client.SendRequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new Exception($"Request `{method}` failed and no fallback hosts available", ex);
            }
        }
    }
    #endregion
    #region Operation
    /// <summary>
    /// Gets the current blockchain header information from active subscription.
    /// Returns null if no block data available yet or subscriptions not active.
    /// </summary>
    public async Task<BlockHeader> GetCurrentBlockAsync(CancellationToken cancellationToken = default)
    {
        if (LastBlock is not null)
        {
            // Optionally warn if data is stale
            var age = (DateTimeOffset.UtcNow - LastBlock.BlockTime);
            if (age > StaleBlockDataThreshold)
                logger.LogWarning("Cached block {Height} is {Age} old, subscriptions may be inactive.", LastBlock.Height, age);
            
            return (LastBlock);
        }
        
        throw new InvalidOperationException("No blockchain data available yet.")
            .AddHelp("Please try again later.");
    }
    #endregion


    #region Helpers
    private IEnumerable<string> GetConfiguredHosts(string[]? hosts)
    {
        if (hosts is not null)
        {
            foreach (var hostEntry in hosts)
            {
                if (!string.IsNullOrWhiteSpace(hostEntry))
                    yield return hostEntry.Trim();
            }
        }
        yield break;
    }
    
    /// <summary>
    /// Attempts to failover to the next configured host.
    /// </summary>
    /// <returns>True if successfully connected to a new host, false if all hosts failed.</returns>
    private async Task<bool> TryFailoverToNextHostAsync(CancellationToken cancellationToken)
    {
        var currentHostIndex = ConfiguredClients.IndexOf(ActiveClient);
        var startIndex = currentHostIndex;
        
        while (true)
        {
            // Move to next host
            currentHostIndex = (currentHostIndex + 1) % ConfiguredClients.Count;
            // If we've tried all hosts, give up
            if (currentHostIndex == startIndex)
            {
                logger.LogError("Failed to connect to any configured ElectrumX host.");
                return false;
            }
            var newClient = ConfiguredClients.ElementAt(currentHostIndex);

            // Connect to fallback client:
            try
            {
                await newClient.Connect(cancellationToken).ConfigureAwait(false);
                
                // Dispose old client
                ElectrumXClient oldClient;
                lock (clientLock)
                {
                    oldClient = ActiveClient;
                    ActiveClient = newClient;
                }
                oldClient?.Dispose();
                                
                // Setup subscriptions on new connection
                await SetupSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
                
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to connect to {Host}, trying next.", newClient);
            }
        }
    }
    
    
    /// <summary>
    /// Parses a block header from ElectrumX response in either Layout1 or Layout2 format.
    /// </summary>
    private static BlockHeader ParseBlockHeader(JsonNode blockData)
    {
        if (blockData.TryDeserialize<BlockResponse.Layout1>(out var layout1))
        {
            var headerBytes = Convert.FromHexString(layout1.Hex);
            return new BlockHeader {
                Height = layout1.Height,
                Hash = ComputeBlockHash(headerBytes),
                BlockTime = ParseBlockTime(headerBytes)
            };
        }
        else if (blockData.TryDeserialize<BlockResponse.Layout2>(out var layout2))
        {
            var hexHeader = ConstructHexHeader(layout2);
            var headerBytes = Convert.FromHexString(hexHeader);
            return new BlockHeader {
                Height = layout2.BlockHeight,
                Hash = ComputeBlockHash(headerBytes),
                BlockTime = DateTimeOffset.FromUnixTimeSeconds(layout2.Timestamp)
            };
        }
        else
            throw new NotImplementedException("Unknown block response layout! Please remove host from server configuration.")
                .AddLogLevel(LogLevel.Critical);
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
            throw new ArgumentException("Invalid block header length!", nameof(headerBytes));

        // Timestamp is at bytes 68-71 (little-endian)
        var timestampBytes = headerBytes[68..72];
        var timestamp = BitConverter.ToUInt32(timestampBytes, 0);

        return (DateTimeOffset.FromUnixTimeSeconds(timestamp));
    }
    private static string ComputeBlockHash(byte[] headerBytes)
    {
        if (headerBytes.Length < 80)
            throw new ArgumentException("Invalid block header length!", nameof(headerBytes));

        var first = SHA256.HashData(headerBytes);
        var second = SHA256.HashData(first);

        // Bitcoin block hash is displayed as reversed byte order
        Array.Reverse(second);

        return (Convert.ToHexString(second).ToLowerInvariant());
    }

    #endregion


    private readonly ILogger<ElectrumXService> logger;
    private readonly ElectrumXSettings settings;
    
    private readonly object clientLock = new object();
    private readonly SemaphoreSlim subscriptionSemaphore = new(1, 1);
    
    private Task? reconnectionTask = null;
    private CancellationTokenSource? reconnectionCts = null;
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