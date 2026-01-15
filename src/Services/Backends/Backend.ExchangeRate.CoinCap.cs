using TeamZaps.Backend;
using TeamZaps.Configuration;
using TeamZaps.Session;
using TeamZaps.Utils;

namespace TeamZaps.Backend;

/// <summary>
/// CoinCap exchange rate backend.
/// </summary>
/// <remarks>
/// - CoinCap API only provides BTC prices in USD. For other currencies, we use static conversion rates.
///   This means non-USD rates may be less accurate than services that provide native multi-currency support.
/// </remarks>
[BackendDescription("CoinCap")]
// CONTRIBUTIONS ARE WELCOME:
// If anyone would like to continue using this API backend, please feel free to implement and test
// the [required API key](https://pro.coincap.io/api-docs).
[Obsolete("This backend is deprecated hence they did not provide a free API anymore. Please use another exchange rate backend.")]
public class CoinCapService : ExchangeRateService
{
    #region Constants.Settings
    static readonly string ApiUrl = "https://api.coincap.io/v2/assets/bitcoin";
    static readonly TimeSpan UpdatePeriod = TimeSpan.FromMinutes(3);

    public override IReadOnlyDictionary<PaymentCurrency, string> SupportedCurrencyCodes { get; } = SupportedCurrencies.ToDictionary(c => c, c => c switch
    {
        PaymentCurrency.Euro => "euro",
        PaymentCurrency.Dollar => "dollar",
        _ => throw new NotSupportedException($"Currency {c} is not supported!")
    });
    #endregion


    public CoinCapService(ILogger<CoinCapService> logger, IHttpClientFactory httpClientFactory, SessionManager sessionManager)
        : base(logger, httpClientFactory, sessionManager, UpdatePeriod)
    {
        // Configure HttpClient with proper headers
        httpClient.DefaultRequestHeaders.Add("User-Agent", "TeamZaps/1.0");
        httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }


    #region Events
    protected override void OnFirstSessionCreated(object? sender, EventArgs e)
    {
        // Update EUR/USD rate when first session is created
        InvokeUpdateFiatConversionRates();

        base.OnFirstSessionCreated(sender, e);
    }
    #endregion


    #region Initialization
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Update fiat rate on startup:
        await UpdateFiatConversionRatesAsync(stoppingToken).ConfigureAwait(false);

        await base.ExecuteAsync(stoppingToken).ConfigureAwait(false);
    }
    #endregion
    #region Operation
    private void InvokeUpdateFiatConversionRates() => Task.Run(() => UpdateFiatConversionRatesAsync(CancellationToken.None));
    private async Task UpdateFiatConversionRatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Fetch conversion rates for all non-USD currencies
            foreach (var currency in SupportedCurrencies)
            {
                if (currency == PaymentCurrency.Dollar)
                {
                    // USD is the base currency, rate is always 1.0
                    lock (usdConversionRates)
                    {
                        usdConversionRates[PaymentCurrency.Dollar] = 1.0;
                    }
                    continue;
                }

                var apiCode = SupportedCurrencyCodes[currency];
                var url = $"https://api.coincap.io/v2/rates/{apiCode}";
                var response = await httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var jsonDoc = JsonSerializer.Deserialize<JsonElement>(response);
                
                if ((jsonDoc.TryGetProperty("data", out var data)) && (data.TryGetProperty("rateUsd", out var rateElement)))
                {
                    var conversionRate = rateElement.GetDouble();
                    lock (usdConversionRates)
                    {
                        usdConversionRates[currency] = conversionRate;
                    }
                    logger.LogDebug("Updated {Currency}/USD conversion rate to {Rate}.", currency, conversionRate);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update conversion rates, using defaults.");
        }
    }

    protected override async Task<Dictionary<PaymentCurrency, double>> FetchRatesAsync(CancellationToken cancellationToken)
    {
        // Get BTC price in USD
        var response = await httpClient.GetStringAsync(ApiUrl, cancellationToken).ConfigureAwait(false);
        var jsonDoc = JsonSerializer.Deserialize<JsonElement>(response);
        
        if ((!jsonDoc.TryGetProperty("data", out var data)) || (!data.TryGetProperty("priceUsd", out var priceElement)))
            throw new Exception("Failed to parse BTC price from CoinCap");
        
        var btcUsd = priceElement.GetDouble();
        var rates = new Dictionary<PaymentCurrency, double>();

        // Calculate BTC rates for all supported currencies using USD conversion rates
        foreach (var currency in SupportedCurrencies)
        {
            double usdRate;
            lock (usdConversionRates)
            {
                if (!usdConversionRates.TryGetValue(currency, out usdRate))
                {
                    logger.LogWarning("No USD conversion rate found for currency '{Currency}'.", currency.GetDescription());
                    continue;
                }
            }
            
            rates[currency] = (btcUsd * usdRate);
        }

        return (rates);
    }
    #endregion
    
    
    // CoinCap only provides USD rates, so we need known exchange rates for other currencies.
    private static readonly Dictionary<PaymentCurrency, double> usdConversionRates = new();
}
