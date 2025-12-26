using System.Text.Json.Serialization;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using teamZaps.Configuration;
using teamZaps.Backend;
using teamZaps.Communication;
using teamZaps.Utils;

namespace teamZaps.Backend;

/// <summary>
/// AlbyHub Lightning backend implementation.
/// </summary>
[BackendDescription("AlbyHub")]
public class AlbyHubService : ILightningBackend, IDisposable
{
    public AlbyHubService(ILoggerFactory loggerFactory, IOptions<AlbyHubSettings> settings, IExchangeRateBackend exchangeRateBackend)
    {
        this.logger = loggerFactory.CreateLogger<AlbyHubService>();
        this.settings = settings.Value;
        this.exchangeRateBackend = exchangeRateBackend;
        this.nostr = new NostrWalletConnector(loggerFactory, settings.Value.ConnectionString, settings.Value.RelayUrls);

        logger.LogInformation("AlbyHub initialized with wallet {WalletPubkey} and {RelayCount} relay(s)", $"{nostr.Pubkey[..8]}...", nostr.Relays.Length);
    }


    #region Properties
    public ulong SentRequests => nostr.SentRequests;
    #endregion


    #region Initialization
    public void Dispose()
    {
        nostr.Dispose();
    }
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

            var response = await nostr.SendNwcRequestAsync<GetBalanceResult>(request, cancellationToken).ConfigureAwait(false);
            if (response is not null)
                return (response.Balance / 1000);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting wallet balance");
        }
        return (null);
    }
    public async Task<ILightningInvoice?> CreateInvoiceAsync(double amount, PaymentCurrency currency, string? memo = null, CancellationToken cancellationToken = default)
    {
        try
        {
            long amountSat;
            if (currency == PaymentCurrency.Sats)
                amountSat = (long)(amount);
            else
                amountSat = exchangeRateBackend.ToSats(amount);

            var request = new NwcRequest
            {
                Method = "make_invoice",
                Params = new MakeInvoiceParams
                {
                    Amount = (amountSat * 1000),
                    Description = memo ?? ""
                }
            };

            var response = await nostr.SendNwcRequestAsync<MakeInvoiceResult>(request, cancellationToken).ConfigureAwait(false);
            if (response is not null)
                return (new AlbyHubInvoice {
                    PaymentRequest = response.Invoice,
                    PaymentHash = response.PaymentHash,
                    SatsAmount = amountSat
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating invoice");
        }
        return (null);
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

            var response = await nostr.SendNwcRequestAsync<PayInvoiceResult>(request, cancellationToken).ConfigureAwait(false);
            if (response is not null)
                return (new AlbyHubPaymentResponse {
                    PaymentHash = response.PaymentHash
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error paying invoice");
        }
        return (null);
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

                var response = await nostr.SendNwcRequestAsync<LookupInvoiceResult>(request, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                var isSettled = ((string.Equals(response.State, "settled", StringComparison.OrdinalIgnoreCase)) || (response.Settled == true));
                return (new AlbyHubPaymentStatus {
                    Paid = isSettled,
                    SatsAmount = (response.Amount / 1000),
                    FiatAmount = 0,
                    FiatRate = 0
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking payment status");
        }
        return (null);
    }
    #endregion


    private readonly ILogger<AlbyHubService> logger;
    private readonly AlbyHubSettings settings;
    private readonly IExchangeRateBackend exchangeRateBackend;
    private readonly NostrWalletConnector nostr;
}

#region Models.Nostr
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
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("invoice")]
    public string Invoice { get; set; } = string.Empty;

    [JsonPropertyName("payment_hash")]
    public string PaymentHash { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <remarks>
    /// Legacy/non-standard field: NIP-47 uses `state` instead. Kept as a fallback.
    /// </remarks>
    [JsonPropertyName("settled")]
    public bool? Settled { get; set; }
}

file class GetBalanceResult
{
    [JsonPropertyName("balance")]
    public long Balance { get; set; }
}
#endregion
#region Models.AlbyHub
file class AlbyHubInvoice : ILightningInvoice
{
    public string PaymentRequest { get; set; } = string.Empty;
    public string PaymentHash { get; set; } = string.Empty;
    long? ILightningInvoice.SatsAmount => SatsAmount;
    public long SatsAmount { get; set; }
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
#endregion
