using System.ComponentModel;
using NLightning.Bolt11.Models;
using teamZaps.Configuration;
using teamZaps.Utils;

namespace teamZaps.Backend;


public static partial class Common
{
    #region Constants
    /// <summary>
    /// Map of available backend types.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (Type Type, Type[] ProvidedInterfaces)> BackendTypes = UtilAssembly
        .GetDefinedTypeMap<BackendDescriptionAttribute>()
        .ToDictionary(
            t => t.Value.BackendType.ToLowerInvariant(),
            t => (t.Key, t.Key.GetInterfaces()
                .Where(i => typeof(IBackend).IsAssignableFrom(i))
                .ToArray()));
    public static readonly string AcceptedFiatPerBitcoinSymbol = $"{BotBehaviorOptions.AcceptedFiatCurrency.ToSymbol()}/{PaymentCurrency.Bitcoin.ToSymbol()}";
    #endregion
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
    ulong SentRequests { get; }
    #endregion
}
/// <summary>
/// Interface for indexer backend services.
/// </summary>
public interface IIndexerBackend : IBackend
{
    IBlockHeader? LastBlock { get; }


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
/// Represents a Bitcoin block.
/// </summary>
public interface IBlockHeader
{
    int Height { get; }
    string Hash { get; }
    public DateTimeOffset BlockTime { get; }
    public DateTimeOffset LocalTime { get; }
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