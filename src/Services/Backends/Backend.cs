using System.ComponentModel;
using NLightning.Bolt11.Models;
using TeamZaps.Configuration;
using TeamZaps.Utils;

namespace TeamZaps.Backends;


/// <summary>
/// Block header information.
/// </summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "$type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor)]
[JsonDerivedType(typeof(BlockHeader), "block")]
[JsonDerivedType(typeof(EstimatedBlockHeader), "estimated")]
public interface IBlockHeader
{
    int Height { get; }
    DateTimeOffset LocalTime { get; }
}
/// <summary>
/// Standard block header implementation.
/// </summary>
public class BlockHeader : IBlockHeader
{
    public int Height { get; set; }
    public string Hash { get; set; } = string.Empty;
    public DateTimeOffset BlockTime { get; set; }
    [JsonIgnore]
    public DateTimeOffset LocalTime => BlockTime.ToLocalTime();


    public override string ToString() => this.Format();
}
/// <summary>
/// Estimated block header implementation.
/// </summary>
public class EstimatedBlockHeader : IBlockHeader
{
    public int Height { get; set; }
    public DateTimeOffset LocalTime { get; set; }


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
    string BackendType => Common.BackendTypes.GetKeyOf(t => (t.Type == this.GetType()));

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

    /// <summary>
    /// Gets whether this host was recently used and should be skipped to allow other hosts to be used (load balancing).
    /// </summary>
    bool RecentlyUsed { get; }
    #endregion
    #region Properties
    string Hostname { get; }
    int Port { get; }
    #endregion
}

/// <summary>
/// Interface for indexer backend services.
/// </summary>
public interface IIndexerBackend : IBackend
{
    BlockHeader? LastBlock { get; }


    Task<IBlockHeader> GetCurrentBlockAsync(CancellationToken cancellationToken = default);
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
    Task<ILightningInvoice?> CreateInvoiceAsync(double amount, PaymentCurrency currency, string? memo = null, CancellationToken cancellationToken = default);
    /// <summary>
    /// Pay a BOLT11 Lightning invoice.
    /// </summary>
    Task<IPaymentResponse?> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default);
    /// <summary>
    /// Check the payment status of an invoice by payment hash.
    /// </summary>
    Task<IPaymentStatus?> CheckPaymentStatusAsync(string paymentHash, CancellationToken cancellationToken = default);

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