using System.Text.Json;
using System.Text.Json.Serialization;
using TeamZaps.Backend;
using TeamZaps.Configuration;
using TeamZaps.Session;
using TeamZaps.Utils;

namespace TeamZaps.Backend;

/// <summary>
/// Yadio exchange rate backend.
/// </summary>
/// <remarks>
/// Provides free, real-time BTC exchange rates for multiple fiat currencies.
/// </remarks>
[BackendDescription("Yadio")]
public class YadioService : ExchangeRateService
{
    #region Constants.Settings
    private const string ApiBaseUrl = "https://api.yadio.io";
    private static readonly TimeSpan UpdatePeriod = TimeSpan.FromMinutes(3);
    
    public override IReadOnlyDictionary<PaymentCurrency, string> SupportedCurrencyCodes { get; } = SupportedCurrencies.ToDictionary(c => c, c => c switch
    {
        PaymentCurrency.Euro => "EUR",
        PaymentCurrency.Dollar => "USD",
        _ => throw new NotSupportedException($"Currency {c} is not supported")
    });
    #endregion


    public YadioService(ILogger<YadioService> logger, IHttpClientFactory httpClientFactory, SessionManager sessionManager)
        : base(logger, httpClientFactory, sessionManager, UpdatePeriod)
    {
    }

    protected override async Task<Dictionary<PaymentCurrency, double>> FetchRatesAsync(CancellationToken cancellationToken)
    {
        var response = await httpClient.GetStringAsync($"{ApiBaseUrl}/exrates/BTC", cancellationToken).ConfigureAwait(false);
        var jsonDoc = JsonSerializer.Deserialize<JsonElement>(response);
        if (!jsonDoc.TryGetProperty("BTC", out var btcRates))
            throw new Exception("Failed to parse exchange rate response");

        var rates = new Dictionary<PaymentCurrency, double>();
        foreach (var currency in SupportedCurrencies)
        {
            if (btcRates.TryGetProperty(SupportedCurrencyCodes[currency], out var rateElement))
                rates[currency] = rateElement.GetDouble();
            else
                logger.LogError("Rate of currency '{Currency}' rate not found in response!", currency.GetDescription());
        }
        return (rates);
    }
}
