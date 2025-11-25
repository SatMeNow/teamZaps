using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using teamZaps.Configuration;

namespace teamZaps.Services;

public class LnbitsService
{
    public LnbitsService(ILogger<LnbitsService> logger, IOptions<LnbitsSettings> settings)
    {
        this.logger = logger;
        this.settings = settings.Value;
        this.httpClient = new();
        {
            httpClient.BaseAddress = new Uri(this.settings.LndhubUrl);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }


    public async Task<LnbitsWalletDetails?> GetWalletDetailsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var res = await RequestAsync<LnbitsWalletDetails>(HttpMethod.Get, "/api/v1/wallet", cancellationToken).ConfigureAwait(false);
            {
                res!.Balance = (res.Balance / 1000); // Convert msat to sat
            }
            return (res);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting wallet details.");
            return (null);
        }
    }
    public Task<LnbitsInvoice?> CreateInvoiceAsync(double amount, string unitName, string? memo = null, CancellationToken cancellationToken = default) => CreateInvoiceAsync(new
    {
        amount = amount,
        unit = unitName,
        memo = memo ?? "",
        @out = false
    }, cancellationToken);
    public Task<LnbitsInvoice?> CreateInvoiceAsync(long amount, string? memo = null, CancellationToken cancellationToken = default) => CreateInvoiceAsync(new
    {
        amount = amount,
        memo = memo ?? "",
        @out = false
    }, cancellationToken);
    private Task<LnbitsInvoice?> CreateInvoiceAsync(object invoiceRequest, CancellationToken cancellationToken)
    {
        try
        {
            return (RequestAsync<LnbitsInvoice>(HttpMethod.Post, "/api/v1/payments", invoiceRequest, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating invoice.");
            return (Task.FromResult<LnbitsInvoice?>(null));
        }
    }
    public async Task<LnbitsPaymentResponse?> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default)
    {
        try
        {
            object payRequest = new
            {
                bolt11 = bolt11,
                @out = true
            };
            var res = await RequestAsync<LnbitsPaymentResponse>(HttpMethod.Post, "/api/v1/payments", payRequest, cancellationToken).ConfigureAwait(false);
            {
                res!.Amount = (res.Amount / 1000); // Convert msat to sat
                res!.Fee = (res.Fee / 1000); // Convert msat to sat
            }
            return (res);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error paying invoice.");
            return (null);
        }
    }
    public async Task<LnbitsPaymentStatus?> CheckPaymentStatusAsync(string paymentHash, CancellationToken cancellationToken = default)
    {
        try
        {
            var res = await RequestAsync<LnbitsPaymentStatus>(HttpMethod.Get, $"/api/v1/payments/{paymentHash}", cancellationToken).ConfigureAwait(false);
            {
                res!.Details!.Amount = (res.Details!.Amount / 1000); // Convert msat to sat
            }
            return (res);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking payment status.");
            return (null);
        }
    }


    #region Helper
    private Task<T?> RequestAsync<T>(HttpMethod method, string? requestUri, CancellationToken cancellationToken) => RequestAsync<T>(method, requestUri, null, cancellationToken);
    private async Task<T?> RequestAsync<T>(HttpMethod method, string? requestUri, object? request, CancellationToken cancellationToken)
    {
        var reqMsg = new HttpRequestMessage(method, requestUri);
        {
            reqMsg.Headers.Add("X-Api-Key", settings.ApiKey);
            if (request is not null)
                reqMsg.Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        }
        var sendRsp = await httpClient.SendAsync(reqMsg, cancellationToken).ConfigureAwait(false);
        sendRsp.EnsureSuccessStatusCode();

        var readRsp = await sendRsp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        logger.LogDebug("Lnbits Response for {Method} '{RequestUri}': {Response}", method, requestUri, readRsp);
        return (JsonSerializer.Deserialize<T>(readRsp));
    }
    #endregion


    private readonly ILogger<LnbitsService> logger;
    private readonly LnbitsSettings settings;
    private readonly HttpClient httpClient;
}

public class LnbitsWalletDetails
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("balance")]
    public long Balance { get; set; }
}

public class LnbitsInvoice
{
    [JsonPropertyName("payment_request")]
    public string PaymentRequest { get; set; } = string.Empty;

    [JsonPropertyName("payment_hash")]
    public string PaymentHash { get; set; } = string.Empty;
}

public class LnbitsPaymentResponse
{
    [JsonPropertyName("payment_hash")]
    public string PaymentHash { get; set; } = string.Empty;

    [JsonPropertyName("payment_request")]
    public string PaymentRequest { get; set; } = string.Empty;

    [JsonPropertyName("checking_id")]
    public string CheckingId { get; set; } = string.Empty;

    [JsonPropertyName("wallet_id")]
    public string WalletId { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("fee")]
    public long Fee { get; set; }

    [JsonPropertyName("memo")]
    public string? Memo { get; set; }
}

public class LnbitsPaymentStatus
{
    [JsonPropertyName("paid")]
    public bool Paid { get; set; }

    [JsonPropertyName("details")]
    public LnbitsPaymentDetails? Details { get; set; }

    [JsonPropertyName("preimage")]
    public string? Preimage { get; set; }
}
public class LnbitsPaymentDetails
{
    [JsonPropertyName("amount")]
    public long Amount { get; set; }
    
    [JsonPropertyName("extra")]
    public LnbitsPaymentExtra? Extra { get; set; }
}
public class LnbitsPaymentExtra
{
    [JsonPropertyName("fiat_amount")]
    public double FiatAmount { get; set; }
    [JsonPropertyName("fiat_currency")]
    public string? FiatCurrency { get; set; }
    [JsonPropertyName("fiat_rate")]
    public double FiatRate { get; set; }
}
