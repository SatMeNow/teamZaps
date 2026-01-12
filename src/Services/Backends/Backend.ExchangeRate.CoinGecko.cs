using teamZaps.Backend;
using teamZaps.Configuration;
using teamZaps.Session;
using teamZaps.Utils;

namespace teamZaps.Backend;

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
    static readonly string ApiUrl = $"https://api.coingecko.com/api/v3/simple/price?ids=bitcoin&vs_currencies={string.Join(",", SupportedCurrencies.Values)}";
    static readonly TimeSpan UpdatePeriod = TimeSpan.FromMinutes(3);
    #endregion


    public CoinGeckoService(ILogger<CoinGeckoService> logger, IHttpClientFactory httpClientFactory, SessionManager sessionManager)
        : base(logger, httpClientFactory, sessionManager, UpdatePeriod)
    {
    }


    protected override async Task<Dictionary<PaymentCurrency, double>> FetchRatesAsync(CancellationToken cancellationToken)
    {
        var response = await httpClient.GetStringAsync(ApiUrl, cancellationToken).ConfigureAwait(false);
        var jsonDoc = JsonSerializer.Deserialize<JsonElement>(response);
        if (!jsonDoc.TryGetProperty("bitcoin", out var bitcoinRates))
            throw new Exception("Failed to parse exchange rate response");

        var rates = new Dictionary<PaymentCurrency, double>();
        foreach (var currency in SupportedCurrencies)
        {
            if (bitcoinRates.TryGetProperty(currency.Value, out var rateElement))
            {
                var rate = rateElement.GetDouble();
                rates[currency.Key] = rate;
            }
            else
            {
                logger.LogError("Rate of currency '{Currency}' rate not found in response!", currency.Key.GetDescription());
            }
        }

        return rates;
    }
}
