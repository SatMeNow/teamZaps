using System.Collections.Concurrent;
using System.Diagnostics;
using teamZaps.Backend;
using teamZaps.Configuration;
using teamZaps.Session;
using teamZaps.Utils;

namespace teamZaps.Backend;

/// <summary>
/// Base class for exchange rate backend services.
/// </summary>
public abstract class ExchangeRateService : BackgroundService, IDisposable, IExchangeRateBackend
{
    #region Constants.Settings
    protected static readonly IReadOnlyDictionary<PaymentCurrency, string> SupportedCurrencies = new Dictionary<PaymentCurrency, string>()
    {
        { PaymentCurrency.Euro, "eur" },
        { PaymentCurrency.Dollar, "usd" }
    };
    #endregion


    protected ExchangeRateService(ILogger logger, IHttpClientFactory httpClientFactory, SessionManager sessionManager, TimeSpan updatePeriod)
    {
        this.logger = logger;
        this.httpClient = httpClientFactory.CreateClient();
        this.sessionManager = sessionManager;
        this.updatePeriod = updatePeriod;
        
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
    protected virtual void OnFirstSessionCreated(object? sender, EventArgs e) => InvokeRefreshRates();
    #endregion


    #region Initialization
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Refresh exchanges rates periodically:
        using var timer = new PeriodicTimer(updatePeriod);
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
    #region Operation
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

            // Fetch rates from derived service:
            var fetchedRates = await FetchRatesAsync(cancellationToken).ConfigureAwait(false);
            SentRequests++;

            // Update cache:
            LastRateUpdate = DateTime.Now;
            rates.Clear();
            foreach (var rate in fetchedRates)
                rates.AddOrUpdate(rate.Key, rate.Value, (key, old) => rate.Value);

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

    /// <summary>
    /// Fetches the latest exchange rates from the API.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary of currency rates (BTC price in fiat).</returns>
    protected abstract Task<Dictionary<PaymentCurrency, double>> FetchRatesAsync(CancellationToken cancellationToken);
    #endregion


    protected readonly ILogger logger;
    protected readonly HttpClient httpClient;
    private readonly SessionManager sessionManager;
    private readonly TimeSpan updatePeriod;
    private CancellationTokenSource forceRefresh;
}