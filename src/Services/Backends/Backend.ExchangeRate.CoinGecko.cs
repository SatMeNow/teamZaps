using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
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
public class CoinGeckoService : BackgroundService, IDisposable, IExchangeRateBackend
{
    #region Constants.Settings
    static readonly IReadOnlyDictionary<PaymentCurrency, string> SupportedCurrencies = new Dictionary<PaymentCurrency, string>()
    {
        { PaymentCurrency.Euro, "eur" },
        { PaymentCurrency.Dollar, "usd" }
    };
    static readonly string ApiUrl = $"https://api.coingecko.com/api/v3/simple/price?ids=bitcoin&vs_currencies={string.Join(",", SupportedCurrencies.Values)}";
    static readonly TimeSpan UpdatePeriod = TimeSpan.FromMinutes(3);
    #endregion


    public CoinGeckoService(ILogger<CoinGeckoService> logger, IHttpClientFactory httpClientFactory, SessionManager sessionManager)
    {
        this.logger = logger;
        this.httpClient = httpClientFactory.CreateClient();
        this.sessionManager = sessionManager;
        
        InvokeRefreshRates();
        Debug.Assert(forceRefresh is not null);

        sessionManager.OnFirstSessionCreated += OnFirstSessionCreated;
    }


    #region Properties
    public long SentRequests { get; private set; }
    public long FailedRequests { get; private set; }

    public IReadOnlyDictionary<PaymentCurrency, double> Rates => rates;
    private ConcurrentDictionary<PaymentCurrency, double> rates = new();
    public DateTime? LastRateUpdate { get; private set; }
    #endregion


    #region Events
    private void OnFirstSessionCreated(object? sender, EventArgs e) => InvokeRefreshRates();
    #endregion


    #region Initialization
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Refresh exchanges rates periodically:
        using var timer = new PeriodicTimer(UpdatePeriod);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!sessionManager.ActiveSessions.IsEmpty())
                await RefreshRatesAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, forceRefresh.Token);
                await timer.WaitForNextTickAsync(delay.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
    public override void Dispose()
    {
        sessionManager.OnFirstSessionCreated -= OnFirstSessionCreated;

        base.Dispose();
    }
    #endregion
    #region Management
    public void InvokeRefreshRates()
    {
        var oldToken = Interlocked.Exchange(ref forceRefresh, new CancellationTokenSource());

        oldToken?.Cancel();
    }
    private async Task RefreshRatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var firstUpdate = (LastRateUpdate is null);

            // Refresh exchange rates:
            var response = await httpClient.GetStringAsync(ApiUrl, cancellationToken).ConfigureAwait(false);
            var jsonDoc = JsonSerializer.Deserialize<JsonElement>(response);
            if (!jsonDoc.TryGetProperty("bitcoin", out var bitcoinRates))
                throw new Exception("Failed to parse exchange rate response");
            SentRequests++;

            // Update cache:
            LastRateUpdate = DateTime.Now;
            rates.Clear();
            foreach (var currency in SupportedCurrencies)
            {
                if (bitcoinRates.TryGetProperty(currency.Value, out var rateElement))
                {
                    var rate = rateElement.GetDouble();
                    rates.AddOrUpdate(currency.Key, rate, (key, old) => rate);
                }
                else 
                {
                    if (firstUpdate)
                    {
                        logger.LogError("Rate of currency '{Currency}' rate not found in response!", currency.Key.GetDescription());
                        return;
                    }
                    else
                        ; // Assume/hope that currency is temporarily not available.
                }
            }

            if ((this as IExchangeRateBackend).FiatRate is null)
                logger.LogWarning("Refreshed exchange rates, but accepted fiat currency rate was not found!");
            else if (firstUpdate)
            {
                logger.LogInformation("Refreshed exchange rates");
                logger.LogInformation("Will drop further logs as long as exchange rates are reliable");
            }
        }
        catch (Exception ex)
        {
            FailedRequests++;
            logger.LogError(ex, "Error refreshing exchange rates");
        }
    }
    #endregion


    private readonly ILogger<CoinGeckoService> logger;
    private readonly HttpClient httpClient;
    private readonly SessionManager sessionManager;
    private CancellationTokenSource forceRefresh;
}
