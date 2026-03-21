using System.ComponentModel;
using System.Reflection;
using NLightning.Bolt11.Models;
using TeamZaps.Configuration;
using TeamZaps.Utils;

namespace TeamZaps.Backends;


public class BlockHeader
{
    public int Height { get; set; }
    public string Hash { get; set; } = string.Empty;
    public DateTimeOffset BlockTime { get; set; }
    [JsonIgnore]
    public DateTimeOffset LocalTime => BlockTime.ToLocalTime();


    public override string ToString() => this.Format();
}

/// <summary>
/// Descriptor for Lightning wallet backend services.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class BackendDescriptionAttribute(string backendType) : Attribute
{
    public string BackendType { get; } = backendType;
}
/// <summary>
/// Interface for backend services.
/// </summary>
public interface IBackend
{
    #region Properties
    /// <summary>
    /// Typename of the backend service.
    /// </summary>
    string BackendType => this.GetType().GetCustomAttribute<BackendDescriptionAttribute>()!.BackendType;

    /// <summary>
    /// Total number of requests sent to the backend.
    /// </summary>
    long SentRequests { get; }
    /// <summary>
    /// Total number of failed requests to the backend.
    /// </summary>
	long FailedRequests { get; }
    #endregion
}
/// <summary>
/// Backend that supports connections to multiple, alternating servers.
/// </summary>
public interface IMultiConnectionBackend : IBackend
{
    #region Properties.Management
    public IReadOnlyCollection<IBackendClient> Hosts { get; }
    #endregion
}
public interface IBackendClient
{
    #region Properties.Management
    /// <inheritdoc cref="IBackend.SentRequests"/>
	long SentRequests { get; }
	/// <inheritdoc cref="IBackend.FailedRequests"/>
	long FailedRequests { get; }

    bool Connected { get; }
    #endregion
    #region Properties
    string Hostname { get; }
    int Port { get; }
    #endregion
}
/// <summary>
/// Backend that supports sanity checks.
/// </summary>
public interface ISanitizableBackend : IBackend
{
    #region Properties
    bool Ready { get; }
    #endregion


    /// <summary>
    /// Performs a sanity check on the backend service.
    /// </summary>
    /// <exception cref="Exception">If the sanity check fails.</exception>
    Task SanityCheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Interface for indexer backend services.
/// </summary>
public interface IIndexerBackend : IBackend
{
    BlockHeader? LastBlock { get; }
    int ReceivedBlocks { get; }


    Task<BlockHeader> GetCurrentBlockAsync(CancellationToken cancellationToken = default);
}
/// <summary>
/// Interface for exchange-rate backend services.
/// </summary>
public interface IExchangeRateBackend : IBackend
{
    #region Constants
    static readonly TimeSpan ReliableOffset = TimeSpan.FromMinutes(5);
    #endregion


    /// <summary>
    /// Timestamp of last successful rate update.
    /// </summary>
    DateTime? LastRateUpdate { get; }
    /// <summary>
    /// Indicates whether the exchange rates are considered reliable (recently updated).
    /// </summary>
    bool RatesReliable => ((LastRateUpdate is not null) && ((DateTime.Now - LastRateUpdate.Value) <= ReliableOffset) && (FiatRate is not null));
    /// <summary>
    /// Contains BTC exchange rate for fiat currencies.
    /// </summary>
    IReadOnlyDictionary<PaymentCurrency, double> Rates { get; }
    /// <summary>
    /// BTC exchange rate for <see cref="BotBehaviorOptions.AcceptedFiatCurrency">accepted fiat currency</see>.
    /// </summary>
    double? FiatRate => Rates.TryGetValue(BotBehaviorOptions.AcceptedFiatCurrency);


    #region Operation
    /// <summary>
    /// Convert fiat amount to sats using the current exchange rate.
    /// </summary>
    /// <exception cref="NullReferenceException"></exception>
    long ToSats(double fiatAmount)
    {
        if (LastRateUpdate is null)
            throw new NullReferenceException($"No exchange rates available!");
        var fiatRate = FiatRate;
        if (fiatRate is null)
            throw new NullReferenceException($"No '{Common.AcceptedFiatPerBitcoinSymbol}' exchange rate available!");

        var amountBtc = (fiatAmount / fiatRate); // Fiat to BTC
        var amountSats = (amountBtc * 100_000_000); // BTC to sats

        return (Convert.ToInt64(amountSats));
    }
    /// <summary>
    /// Convert fiat amount to msats using the current exchange rate.
    /// </summary>
    /// <exception cref="NullReferenceException"></exception>
    long ToMsats(double fiatAmount) => ToSats(fiatAmount * 1000);
    #endregion
}
/// <summary>
/// Interface for Lightning wallet backend services.
/// </summary>
public interface ILightningBackend : IBackend
{
    #region Operation.Invoice
    /// <summary>
    /// Create a Lightning invoice.
    /// </summary>
    Task<ILightningInvoice> CreateInvoiceAsync(double amount, PaymentCurrency currency, string? memo = null, CancellationToken cancellationToken = default);
    /// <inheritdoc cref="CreateInvoiceAsync(double, PaymentCurrency, string?, CancellationToken)"/> 
    Task<ILightningInvoice> CreateInvoiceAsync(long amount, string? memo = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pay a BOLT11 Lightning invoice.
    /// </summary>
    Task<IPaymentResponse> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default);
    /// <summary>
    /// Check the payment status of an invoice by payment hash.
    /// </summary>
    Task<IPaymentStatus> CheckPaymentStatusAsync(string paymentHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decodes a lightning invoice to get the amount in sats.
    /// </summary>
    /// <throw cref="NullReferenceException"></throw>
    long GetInvoiceAmount(string bolt11)
    {
        var sats = Invoice.Decode(bolt11)?.Amount?.Satoshi;
        if (sats is null)
            throw new NullReferenceException("Failed to decode lightning invoice!\n\n" +
                "Please provide a valid bolt11 invoice.")
                .AddLogLevel(LogLevel.Warning);
        else
            return (sats!.Value);
    }
    #endregion
}
public interface ISupportsCancelInvoice : ILightningBackend
{
    /// <summary>
    /// Cancel an unpaid Lightning invoice by payment hash.
    /// </summary>
    Task<bool> CancelInvoiceAsync(string paymentHash, CancellationToken cancellationToken = default);
}
/// <summary>
/// Interface for Cashu mint backend services.
/// Extends <see cref="ILightningBackend"/> with eCash wallet operations:
/// NUT-04 (mint eCash from Lightning payments) and NUT-05 (melt eCash to pay Lightning invoices).
/// </summary>
public interface ICashuBackend : ILightningBackend
{
    /// <summary>The Cashu mint URL.</summary>
    string MintUrl { get; }
    /// <summary>
    /// Minimum sats the wallet must hold before new sessions are permitted.
    /// Covers the <c>fee_reserve</c> the mint charges on NUT-05 melt operations.
    /// </summary>
    long MinimumReserve { get; }
    /// <summary>
    /// Get the current eCash wallet balance in sats.
    /// </summary>
    Task<long> GetBalanceAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Receive a serialized Cashu token (cashuA/cashuB), validate and absorb its proofs into the bot's wallet
    /// via NUT-03 swap (atomically burns user proofs and issues fresh ones, preventing double-spend).
    /// Throws if the token is from a different mint, already spent, or malformed.
    /// </summary>
    /// <returns>Total sats received.</returns>
    Task<long> ReceiveTokenAsync(string cashuToken, CancellationToken cancellationToken = default);
    /// <summary>
    /// Create a serialized cashuA token of exactly <paramref name="sats"/> from the bot's wallet
    /// and return it as a string. The selected proofs are removed from the wallet.
    /// </summary>
    Task<string> SendTokenAsync(long sats, CancellationToken cancellationToken = default);
    /// <summary>
    /// Query the Cashu melt fee for a given BOLT11 invoice without paying it (NUT-05 quote).
    /// </summary>
    /// <returns>The <c>fee_reserve</c> in sats the mint will charge on top of the invoice amount.</returns>
    Task<long> QueryMeltFeeAsync(string bolt11, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lightning invoice details (BOLT11 payment request).
/// </summary>
public interface ILightningInvoice
{
    string PaymentRequest { get; }
    string PaymentHash { get; }
    public long? SatsAmount { get; }
}

/// <summary>
/// Payment response after paying an invoice.
/// </summary>
public interface IPaymentResponse
{
    string PaymentHash { get; }
    long Amount { get; }
    long Fee { get; }
}

/// <summary>
/// Payment status check result.
/// </summary>
public interface IPaymentStatus
{
    bool Paid { get; }
    long SatsAmount { get; }
    double FiatAmount { get; }
    double FiatRate { get; }
}

internal static partial class Ext
{
    public static T? GetOptionalBackend<T>(this IEnumerable<IBackend> source)
        where T : IBackend
    {
        return (source.OfType<T>().FirstOrDefault());
    }
    public static T GetMandatoryBackend<T>(this IEnumerable<IBackend> source)
        where T : IBackend
    {
        return (source.OfType<T>().First());
    }
}