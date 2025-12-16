using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using teamZaps.Configuration;
using teamZaps.Backend;
using teamZaps.Utils;

namespace teamZaps.Backend;

/// <summary>
/// AlbyHub Lightning backend implementation.
/// </summary>
[BackendDescription("AlbyHub")]
public class AlbyHubService : ILightningBackend, IAsyncDisposable
{
    public AlbyHubService(ILogger<AlbyHubService> logger, IOptions<AlbyHubSettings> settings)
    {
        this.logger = logger;
        this.settings = settings.Value;

        // Parse NWC connection string:
        var connectionUri = new Uri(this.settings.ConnectionString);
        if (connectionUri.Scheme != "nostr+walletconnect")
            throw new InvalidOperationException("Invalid NWC connection string. Must start with nostr+walletconnect://");
        walletPubkey = connectionUri.Host;
        walletPubkeyBytes = Convert.FromHexString(walletPubkey);

        // Extract secret and relay parameters:
        var queryParams = ParseQueryString(connectionUri.Query);
        secret = (queryParams.TryGetValue("secret", out var secretValue)) && (secretValue.Count > 0)
            ? secretValue[0]
            : throw new InvalidOperationException("NWC connection string missing 'secret' parameter");
        clientPrivateKey = Context.Instance.CreateECPrivKey(Convert.FromHexString(secret));
        clientPublicKey = Convert.ToHexString(clientPrivateKey.CreateXOnlyPubKey().ToBytes()).ToLower();

        // Determine relay URLs:
        if ((this.settings.RelayUrls is not null) && (this.settings.RelayUrls.Length > 0))
            relays = this.settings.RelayUrls;
        else if ((queryParams.TryGetValue("relay", out var relayValues)) && (relayValues.Count > 0))
            relays = relayValues.ToArray();
        else
            throw new InvalidOperationException("NWC connection string missing 'relay' parameter(s)");

        // Initialize Nostr client:
        var relayUris = relays
            .Select(r => new Uri(r))
            .ToArray();
        nostrClient = new NostrClient(relayUris[0]);
        foreach (var relay in relayUris.Skip(1))
        {
        }

        logger.LogInformation("AlbyHub initialized with wallet {WalletPubkey} and {RelayCount} relay(s)", walletPubkey[..8] + "...", relays.Length);
    }


    #region Properties
    public ulong SentRequests { get; private set; }
    #endregion


    #region Operation.Invoice
    public async Task<long?> GetBalanceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new NwcRequest
            {
                Method = "get_balance",
                Params = new { }
            };

            var response = await SendNwcRequestAsync<GetBalanceResult>(request, cancellationToken);
            if (response is null)
                return (null);

            return (response.Balance / 1000);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting wallet balance from AlbyHub");
            return (null);
        }
    }
    public async Task<ILightningInvoice?> CreateInvoiceAsync(double amount, PaymentCurrency currency, string? memo = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var amountMsat = (currency == PaymentCurrency.Sats)
                ? (long)(amount * 1000)
                : throw new NotSupportedException($"Currency '{currency.GetDescription()}' not supported by AlbyHub");
            var request = new NwcRequest
            {
                Method = "make_invoice",
                Params = new MakeInvoiceParams
                {
                    Amount = amountMsat,
                    Description = memo ?? ""
                }
            };

            var response = await SendNwcRequestAsync<MakeInvoiceResult>(request, cancellationToken);
            if (response is null)
                return (null);

            return (new AlbyHubInvoice
            {
                PaymentRequest = response.Invoice,
                PaymentHash = response.PaymentHash
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating invoice via AlbyHub");
            return (null);
        }
    }
    public async Task<IDecodedInvoice?> DecodeInvoiceAsync(string bolt11, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new NwcRequest
            {
                Method = "lookup_invoice",
                Params = new LookupInvoiceParams
                {
                    Invoice = bolt11
                }
            };

            var response = await SendNwcRequestAsync<LookupInvoiceResult>(request, cancellationToken);
            if (response is null)
                return (null);

            return (new AlbyHubDecodedInvoice
            {
                Amount = response.Amount / 1000,
                Description = response.Description,
                PaymentHash = response.PaymentHash
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error decoding invoice via AlbyHub: {Invoice}", bolt11);
            return (null);
        }
    }
    public async Task<IPaymentResponse?> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new NwcRequest
            {
                Method = "pay_invoice",
                Params = new PayInvoiceParams
                {
                    Invoice = bolt11
                }
            };

            var response = await SendNwcRequestAsync<PayInvoiceResult>(request, cancellationToken);
            if (response is null)
                return (null);

            return (new AlbyHubPaymentResponse
            {
                PaymentHash = response.PaymentHash,
                Amount = response.Amount / 1000,
                Fee = response.FeesPaid / 1000
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error paying invoice via AlbyHub");
            return (null);
        }
    }
    public async Task<IPaymentStatus?> CheckPaymentStatusAsync(string paymentHash, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new NwcRequest
            {
                Method = "lookup_invoice",
                Params = new LookupInvoiceParams
                {
                    PaymentHash = paymentHash
                }
            };

            var response = await SendNwcRequestAsync<LookupInvoiceResult>(request, cancellationToken);
            if (response is null)
                return (null);

            return (new AlbyHubPaymentStatus
            {
                Paid = response.Settled,
                SatsAmount = response.Amount / 1000,
                FiatAmount = 0,
                FiatRate = 0
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking payment status via AlbyHub");
            return (null);
        }
    }
    #endregion


    #region Helper
    private static Dictionary<string, List<string>> ParseQueryString(string query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
            return (result);

        var queryWithoutPrefix = query.TrimStart('?');
        var pairs = queryWithoutPrefix.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            if (!result.ContainsKey(key))
                result[key] = new List<string>();
            result[key].Add(value);
        }

        return (result);
    }
    private async Task<TResult?> SendNwcRequestAsync<TResult>(object request, CancellationToken cancellationToken) where TResult : class
    {
        try
        {
            await nostrClient.ConnectAndWaitUntilConnected(cancellationToken);
            // Serialize request:
            var requestJson = JsonSerializer.Serialize(request);
            
            // Encrypt content using NIP-04:
            var encryptedContent = EncryptNip04(requestJson, walletPubkeyBytes);

            // Create and sign Nostr event:
            var evt = new NostrEvent
            {
                Kind = 23194,
                Content = encryptedContent,
                CreatedAt = DateTimeOffset.UtcNow,
                Tags = new List<NostrEventTag>
                {
                    new NostrEventTag { TagIdentifier = "p", Data = new List<string> { walletPubkey } }
                }
            };
            await evt.ComputeIdAndSignAsync(clientPrivateKey);

            // Subscribe to response:
            var responseReceived = new TaskCompletionSource<NostrEvent>();
            var subscriptionId = Guid.NewGuid().ToString();
            
            void OnEventsReceived(object? sender, (string subscriptionId, NostrEvent[] events) args)
            {
                if (args.subscriptionId == subscriptionId)
                {
                    foreach (var responseEvent in args.events)
                    {
                        var eTag = responseEvent.GetTaggedData("e");
                        if ((responseEvent.Kind == 23195) && (eTag.Length > 0) && (eTag[0] == evt.Id))
                        {
                            responseReceived.TrySetResult(responseEvent);
                            return;
                        }
                    }
                }
            }

            nostrClient.EventsReceived += OnEventsReceived;
            
            try
            {
                // Subscribe before sending:
                await nostrClient.CreateSubscription(subscriptionId, new[]
                {
                    new NostrSubscriptionFilter
                    {
                        Kinds = new[] { 23195 },
                        Authors = new[] { walletPubkey }
                    }
                });

                // Send request:
                await nostrClient.PublishEvent(evt, cancellationToken);
                logger.LogDebug("Sent NWC request {EventId} to AlbyHub", evt.Id);

                // Wait for response with timeout:
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                
                var responseEvent = await responseReceived.Task.WaitAsync(linkedCts.Token);

                // Decrypt and parse response:
                var decryptedContent = DecryptNip04(responseEvent!.Content!, walletPubkeyBytes);
                var response = JsonSerializer.Deserialize<NwcResponse<TResult>>(decryptedContent);

                if ((response is not null) && (response.Error is not null))
                {
                    logger.LogError("AlbyHub returned error: {Code} - {Message}", response.Error.Code, response.Error.Message);
                    return (null);
                }

                return (response?.Result);
            }
            finally
            {
                nostrClient.EventsReceived -= OnEventsReceived;
                await nostrClient.CloseSubscription(subscriptionId);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("AlbyHub request timed out");
            return (null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending request to AlbyHub");
            return (null);
        }
    }
    
    private string EncryptNip04(string plaintext, byte[] recipientPubkey)
    {
        // Compute shared secret (ECDH):
        // Nostr uses 32-byte X-only pubkeys, need to convert to 33-byte compressed (0x02 prefix for even Y)
        var compressedPubkey = new byte[33];
        compressedPubkey[0] = 0x02;
        Array.Copy(recipientPubkey, 0, compressedPubkey, 1, 32);
        var sharedPoint = ECPubKey.Create(compressedPubkey).GetSharedPubkey(clientPrivateKey);
        var sharedSecret = sharedPoint.ToBytes()[1..33];

        // Generate IV:
        var iv = RandomUtils.GetBytes(16);

        // Encrypt:
        using var aes = Aes.Create();
        aes.Key = sharedSecret;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        
        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        // Return base64(ciphertext)?iv=base64(iv):
        return ($"{Convert.ToBase64String(ciphertext)}?iv={Convert.ToBase64String(iv)}");
    }

    private string DecryptNip04(string encryptedContent, byte[] senderPubkey)
    {
        // Parse content:
        var parts = encryptedContent.Split("?iv=");
        if (parts.Length != 2)
            throw new InvalidOperationException("Invalid encrypted content format");

        var ciphertext = Convert.FromBase64String(parts[0]);
        var iv = Convert.FromBase64String(parts[1]);

        // Compute shared secret (ECDH):
        // Nostr uses 32-byte X-only pubkeys, need to convert to 33-byte compressed (0x02 prefix for even Y)
        var compressedPubkey = new byte[33];
        compressedPubkey[0] = 0x02;
        Array.Copy(senderPubkey, 0, compressedPubkey, 1, 32);
        var sharedPoint = ECPubKey.Create(compressedPubkey).GetSharedPubkey(clientPrivateKey);
        var sharedSecret = sharedPoint.ToBytes()[1..33];

        // Decrypt:
        using var aes = Aes.Create();
        aes.Key = sharedSecret;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);

        return (Encoding.UTF8.GetString(plaintext));
    }
    #endregion

    public ValueTask DisposeAsync()
    {
        nostrClient?.Dispose();
        return ValueTask.CompletedTask;
    }


    private readonly ILogger<AlbyHubService> logger;
    private readonly AlbyHubSettings settings;
    private readonly NostrClient nostrClient;
    private readonly string walletPubkey;
    private readonly byte[] walletPubkeyBytes;
    private readonly string secret;
    private readonly ECPrivKey clientPrivateKey;
    private readonly string clientPublicKey;
    private readonly string[] relays;
}

#region NWC Request/Response Models
file class NwcRequest
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public object Params { get; set; } = new();
}

file class NwcResponse<T>
{
    [JsonPropertyName("result_type")]
    public string ResultType { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public T? Result { get; set; }

    [JsonPropertyName("error")]
    public NwcError? Error { get; set; }
}

file class NwcError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

file class MakeInvoiceParams
{
    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

file class MakeInvoiceResult
{
    [JsonPropertyName("invoice")]
    public string Invoice { get; set; } = string.Empty;

    [JsonPropertyName("payment_hash")]
    public string PaymentHash { get; set; } = string.Empty;
}

file class PayInvoiceParams
{
    [JsonPropertyName("invoice")]
    public string Invoice { get; set; } = string.Empty;
}

file class PayInvoiceResult
{
    [JsonPropertyName("preimage")]
    public string Preimage { get; set; } = string.Empty;

    [JsonPropertyName("payment_hash")]
    public string PaymentHash { get; set; } = string.Empty;
    
    [JsonPropertyName("amount")]
    public long Amount { get; set; }
    
    [JsonPropertyName("fees_paid")]
    public long FeesPaid { get; set; }
}

file class LookupInvoiceParams
{
    [JsonPropertyName("invoice")]
    public string? Invoice { get; set; }

    [JsonPropertyName("payment_hash")]
    public string? PaymentHash { get; set; }
}

file class LookupInvoiceResult
{
    [JsonPropertyName("invoice")]
    public string Invoice { get; set; } = string.Empty;

    [JsonPropertyName("payment_hash")]
    public string PaymentHash { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("settled")]
    public bool Settled { get; set; }
}

file class GetBalanceResult
{
    [JsonPropertyName("balance")]
    public long Balance { get; set; }
}
#endregion

#region Interface Implementations
file class AlbyHubInvoice : ILightningInvoice
{
    public string PaymentRequest { get; set; } = string.Empty;
    public string PaymentHash { get; set; } = string.Empty;
}

file class AlbyHubPaymentResponse : IPaymentResponse
{
    public string PaymentHash { get; set; } = string.Empty;
    public long Amount { get; set; }
    public long Fee { get; set; }
}

file class AlbyHubPaymentStatus : IPaymentStatus
{
    public bool Paid { get; set; }
    public long SatsAmount { get; set; }
    public double FiatAmount { get; set; }
    public double FiatRate { get; set; }
}

file class AlbyHubDecodedInvoice : IDecodedInvoice
{
    public long Amount { get; set; }
    public string? Description { get; set; }
    public string? PaymentHash { get; set; }
}
#endregion
