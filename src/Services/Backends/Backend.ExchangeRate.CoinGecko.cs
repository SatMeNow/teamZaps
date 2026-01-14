using TeamZaps.Backend;
using TeamZaps.Configuration;
using TeamZaps.Session;
using TeamZaps.Utils;

namespace TeamZaps.Backend;

/// <summary>
/// CoinGecko exchange rate backend.
/// </summary>
[BackendDescription("CoinGecko")]
// CONTRIBUTIONS ARE WELCOME:
// If anyone would like to continue using this API backend, please feel free to implement and test
// the [required API key](https://docs.coingecko.com/reference/setting-up-your-api-key).
[Obsolete("This backend is deprecated hence they did not provide a free API anymore. Please use another exchange rate backend.")]
public class CoinGeckoService : ExchangeRateService
{
    #region Constants.Settings
    static readonly string ApiUrl = "https://api.coingecko.com/api/v3/simple/price?ids=bitcoin&vs_currencies={0}";
    static readonly TimeSpan UpdatePeriod = TimeSpan.FromMinutes(3);
    
    public override IReadOnlyDictionary<PaymentCurrency, string> SupportedCurrencyCodes { get; } = SupportedCurrencies.ToDictionary(c => c, c => c switch
    {
        PaymentCurrency.Euro => "eur",
        PaymentCurrency.Dollar => "usd",
        _ => throw new NotSupportedException($"Currency {c} is not supported")
    });
    #endregion


    public CoinGeckoService(ILogger<CoinGeckoService> logger, IHttpClientFactory httpClientFactory, SessionManager sessionManager)
        : base(logger, httpClientFactory, sessionManager, UpdatePeriod)
    {
    }


    protected override async Task<Dictionary<PaymentCurrency, double>> FetchRatesAsync(CancellationToken cancellationToken)
    {
        var currencies = string.Join(",", SupportedCurrencyCodes.Values);
        var apiUrl = string.Format(ApiUrl, currencies);
        
        var response = await httpClient.GetStringAsync(apiUrl, cancellationToken).ConfigureAwait(false);
        var jsonDoc = JsonSerializer.Deserialize<JsonElement>(response);
        if (!jsonDoc.TryGetProperty("bitcoin", out var bitcoinRates))
            throw new Exception("Failed to parse exchange rate response");

        var rates = new Dictionary<PaymentCurrency, double>();
        foreach (var currency in SupportedCurrencies)
        {
            if (bitcoinRates.TryGetProperty(SupportedCurrencyCodes[currency], out var rateElement))
            {
                var rate = rateElement.GetDouble();
                rates[currency] = rate;
            }
            else
                logger.LogError("Rate of currency '{Currency}' rate not found in response!", currency.GetDescription());
        }

        return rates;
    }
}
