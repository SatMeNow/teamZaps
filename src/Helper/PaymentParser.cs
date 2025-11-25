using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic;
using teamZaps.Configuration;
using teamZaps.Utils;

namespace teamZaps.Sessions;

public enum PaymentStatus
{
    [Icon("⏳"), Description("Pay this invoice to add your contribution to the session!")]
    Pending,
    [Icon("✅"), Description("Thank you! Your payment has been confirmed.")]
    Paid,
    [Icon("❌"), Description("This invoice has expired.")]
    Expired
}
public enum PaymentCurrency
{
    [Description("Satoshis"), Currency("ⓢ", "sat", [ "s", "sat", "sats" ])] // Alternative signs: ⓢ ₛ 𝕤
    Sats,
    [Description("Euro"), Currency("€", "EUR", [ "eur", "euro" ])]
    Euro,
    [Description("US Dollar"), Currency("$", "USD", [ "usd" ])]
    Dollar
}

public interface IFormattableAmount
{
    long SatsAmount { get; }
    double FiatAmount { get; }
}

public record PaymentToken : IFormattableAmount
{
    public required decimal Amount;
    public required PaymentCurrency Currency;
    public string? Note;

    public long SatsAmount => Currency.Equals(PaymentCurrency.Sats) ? (long)Amount : 0;
    public double FiatAmount => Currency.Equals(PaymentCurrency.Sats) ? 0 : (double)Amount;
}

public static class PaymentParser
{
    private static readonly Regex TokenRegex = new(
        @"(?<amount>[0-9]+(?:[\.,][0-9]+)?)\s*(?<currency>sat|sats|eur|€|usd|\$)?(?:\s+(?<note>[^+]+?))?(?=\s*\+|$)",
        (RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace)
    );


    public static bool TryParse(string input, out List<PaymentToken> tokens, out string? error)
    {
        tokens = new List<PaymentToken>();
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Payment string is empty.";
            return false;
        }

        var matches = TokenRegex.Matches(input);
        if (matches.IsEmpty())
        {
            error = "No valid payment tokens found.";
            return false;
        }

        foreach (Match match in matches)
        {
            if (!match.Success)
                continue;

            var amountStr = match.Groups["amount"].Value.Replace(',', '.');
            if (!decimal.TryParse(amountStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                error = $"Invalid amount: {amountStr}";
                return false;
            }

            var currencyRaw = match.Groups["currency"].Value.ToLowerInvariant();
            var currency = currencyRaw.ToCurrency();
            if (currency is null)
            {
                error = $"Unsupported currency: {currencyRaw}";
                return false;
            }
            var note = match.Groups.TryGetValue("note");

            tokens.Add(new PaymentToken() {
                Amount = amount,
                Currency = currency!.Value,
                Note = note?.Trim()
            });
        }

        if (tokens.IsEmpty())
        {
            error = "No payment amounts parsed.";
            return false;
        }

        return true;
    }
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
    public static string? FormatAmount(this IFormattableAmount source)
    {
        var sats = source.SatsAmount.Format();
        var fiat = source.FiatAmount.Format();

        if ((sats is null) && (fiat is null))
            return (null);
        else if ((sats is not null) && (fiat is not null))
            return ($"*{sats}* / {fiat}");
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
}