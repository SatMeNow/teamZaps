using System.ComponentModel;
using teamZaps.Utils;

namespace teamZaps.Backend;


public static partial class Common
{
    /// <summary>
    /// Map of available backend types.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Type> BackendTypes = UtilAssembly
        .GetDefinedTypeMap<BackendDescriptionAttribute>()
        .ToDictionary(t => t.Value.BackendType.ToLowerInvariant(), t => t.Key);
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
/// Interface for Lightning wallet backend services.
/// </summary>
public interface ILightningBackend
{
    #region Properties
    /// <summary>
    /// Typename of the backend service.
    /// </summary>
    string BackendType => Common.BackendTypes.GetKeyOf(this.GetType());

    /// <summary>
    /// Total number of requests sent to the backend.
    /// </summary>
    ulong SentRequests { get; }
    #endregion


    #region Operation.Invoice
    /// <summary>
    /// Create a Lightning invoice.
    /// </summary>
    Task<ILightningInvoice?> CreateInvoiceAsync(double amount, PaymentCurrency currency, string? memo = null, CancellationToken cancellationToken = default);
    /// <summary>
    /// Decode a BOLT11 Lightning invoice to extract payment details.
    /// </summary>
    Task<IDecodedInvoice?> DecodeInvoiceAsync(string bolt11, CancellationToken cancellationToken = default);
    /// <summary>
    /// Pay a BOLT11 Lightning invoice.
    /// </summary>
    Task<IPaymentResponse?> PayInvoiceAsync(string bolt11, CancellationToken cancellationToken = default);
    /// <summary>
    /// Check the payment status of an invoice by payment hash.
    /// </summary>
    Task<IPaymentStatus?> CheckPaymentStatusAsync(string paymentHash, CancellationToken cancellationToken = default);
    #endregion
}

/// <summary>
/// Lightning invoice details (BOLT11 payment request).
/// </summary>
public interface ILightningInvoice
{
    string PaymentRequest { get; }
    string PaymentHash { get; }
}

/// <summary>
/// Decoded Lightning invoice information.
/// </summary>
public interface IDecodedInvoice
{
    long Amount { get; }
    string? Description { get; }
    string? PaymentHash { get; }
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
