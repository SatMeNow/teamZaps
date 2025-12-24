using System.ComponentModel;
using System.Numerics;
using teamZaps.Backend;
using teamZaps.Configuration;
using teamZaps.Utils;

namespace teamZaps;


[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class IconAttribute : Attribute
{
    public IconAttribute(string icon)
    {
        this.Icon = icon;
    }

    public string Icon { get; }
}
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class CurrencyAttribute : Attribute, IEquatable<string>
{
    public CurrencyAttribute(string Symbol, string unitName, string[] abbreviations)
    {
        this.Symbol = Symbol;
        this.UnitName = unitName;
        this.Abbreviations = abbreviations;
    }

    public string Symbol { get; }
    public string UnitName { get; }
    public string[] Abbreviations { get; }

    public bool Equals(string? other)
    {
        if (Symbol.Equals(other))
            return (true);
        if (Abbreviations.Contains(other?.ToLowerInvariant()))
            return (true);

        return (false);
    }
}

public enum PaymentStatus
{
    [Icon("⏳"), Description("*Pay this invoice* to add your contribution to the session!")]
    Pending,
    [Icon("✅"), Description("*Thank you!* Your payment has been confirmed.")]
    Paid,
    [Icon("❌"), Description("This invoice has *expired*.")]
    Expired
}
public enum PaymentCurrency
{
    [Description("Satoshis"), Currency("丰", "sat", [ "s", "sat", "sats" ])] // Alternative signs: ⓢ ₛ 𝕤 丰
    Sats,
    [Description("Bitcoin"), Currency("₿", "BTC", [ "btc", "bitcoin" ])]
    Bitcoin,
    [Description("Euro"), Currency("€", "EUR", [ "eur", "euro" ])]
    Euro,
    [Description("US Dollar"), Currency("$", "USD", [ "usd" ])]
    Dollar,

    [Description("Cent"), Currency("¢", "", [ "c", "cnt", "cent", "cents" ])]
    Cent
}

public interface IFormattableAmount
{
    long SatsAmount { get; }
    double FiatAmount { get; }
}
public interface ITipableAmount : IFormattableAmount
{
    double TipAmount { get; }
    double TotalFiatAmount => (TipAmount + FiatAmount);
}


public static partial class Extensions
{
    #region Constants
    private static readonly IReadOnlyDictionary<PaymentStatus, IconAttribute> IconMap = UtilEnum.GetCustomAttributes<PaymentStatus, IconAttribute>();
    private static readonly IReadOnlyDictionary<PaymentCurrency, CurrencyAttribute> CurrencyMap = UtilEnum.GetCustomAttributes<PaymentCurrency, CurrencyAttribute>();
    #endregion


    public static string GetIcon(this PaymentStatus source) => (IconMap.TryGetValue(source, out var icon) ? icon.Icon : "");

    public static PaymentCurrency? ToCurrency(this string? source)
    {
        if (string.IsNullOrEmpty(source))
            // Return default currency:
            return (BotBehaviorOptions.AcceptedFiatCurrency);

        return (CurrencyMap.TryGetKeyOf(c => c.Equals(source), out var currency) ? currency : null);
    }
    public static string ToSymbol(this PaymentCurrency source) => (CurrencyMap.TryGetValue(source, out var attr) ? attr.Symbol : "");
    public static string ToUnitName(this PaymentCurrency source) => (CurrencyMap.TryGetValue(source, out var attr) ? attr.UnitName : "");
    public static IEnumerable<string> GetAbbreviations(this PaymentCurrency source)
    {
        if (CurrencyMap.TryGetValue(source, out var attr))
            return (attr.Abbreviations.Prepend(attr.Symbol));
        else
            return (Enumerable.Empty<string>());
    }

    public static string FormatTip(this byte? source) => FormatTip(source ?? 0);
    public static string FormatTip(this byte source) => (source <= 0) ? "🚫 None" : $"{source}%";

    public static string? FormatTotalFiatAmount(this ITipableAmount source)
    {
        var amount = $"*{source.TotalFiatAmount.Format()}*";
        var tipAmount = source.TipAmount;
        if (tipAmount > 0.01)
            amount += $" (inkl. {tipAmount.Format()} tip)";
        return (amount);
    }
    public static string FormatFiatRate(this double source) => $"{source:F2} {Common.AcceptedFiatPerBitcoinSymbol}";
    public static string? FormatAmount(this IFormattableAmount source)
    {
        var sats = source.SatsAmount.Format();
        string? fiat;
        if (source is ITipableAmount tip)
            fiat = tip.TotalFiatAmount.Format();
        else
            fiat = source.FiatAmount.Format();

        if ((sats is null) && (fiat is null))
            return (null);
        else if ((sats is not null) && (fiat is not null))
            return ($"*{sats}* ({fiat})");
        else
            return (sats ?? fiat);
    }
    public static string? Format(this long source) => Format(source, PaymentCurrency.Sats);
    public static string? Format(this double source) => Format(source, BotBehaviorOptions.AcceptedFiatCurrency);
    public static string? Format<T>(this INumber<T> source, PaymentCurrency currency)
        where T : INumber<T>
    {
        if (T.Zero.Equals(source))
            return (null);
        
        if (currency == PaymentCurrency.Sats)
            return ($"{source}{currency.ToSymbol()}");
        else
            return ($"{source:F2}{currency.ToSymbol()}");
    }
    public static string? Format(this IBlockHeader source) => $"{source.FormatHeight()} ({source.BlockTime:G})";
    public static string? FormatHeight(this IBlockHeader source) => $"[{source.Height.ToString("N0")}](https://mempool.space/block/{source.Hash})";
}
