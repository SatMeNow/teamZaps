using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using TeamZaps.Utils;

namespace TeamZaps.Payment;

public record PaymentToken : IFormattableAmount
{
    public required decimal Amount;
    public required PaymentCurrency Currency;
    public string? Note;

    public long SatsAmount => Currency.Equals(PaymentCurrency.Sats) ? (long)Amount : 0;
    public double FiatAmount => Currency.Equals(PaymentCurrency.Sats) ? 0 : (double)Amount;


    public override string ToString()
    {
        var memo = string.IsNullOrWhiteSpace(Note) ? "" : $" - {Note}";
        return $"{Amount.Format(Currency, false)!}{memo}";
    }
}

public static class PaymentParser
{
    private static readonly PaymentCurrency[] ParsedCurrencies = [ PaymentCurrency.Euro , PaymentCurrency.Cent];
    private static readonly string CurrencyAbbreviations = string.Join("|", ParsedCurrencies
        .Select(c => c.GetAbbreviations())
        .SelectMany(a => a));
    private static readonly Regex TokenRegex = new(
        $@"(?<amount>[0-9]+(?:[\.,][0-9]+)?)\s*(?<currency>{CurrencyAbbreviations})?(?:\s+(?<note>[^+\r\n]+?))?(?=\s*[\+\r\n]|$)",
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

            // Convert if required:
            if (currency == PaymentCurrency.Cent)
            {
                amount = (amount / 100m);
                currency = PaymentCurrency.Euro;
            }

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
    public static bool IsLightningInvoice(this string source, [NotNullWhen(true)] out string? parsedInvoice)
    {
        parsedInvoice = null;

        var invoice = source
            .ToLower()
            .Replace("lightning:", "");
        if (!invoice.StartsWith("ln"))
            return (false);
        if (invoice.Length < 50)
            return (false);
        if (!invoice
            .Select(char.ToLowerInvariant)
            .All(c => 'a' <= c && c <= 'z' ||
                      '0' <= c && c <= '9'))
            return (false);
            
        parsedInvoice = invoice;
        return (true);
    }
    public static bool IsPaymentRequest(this string source)
    {
        source = source.ToLower();
        if (!source.StartsWith("lnbc"))
            return (false);
        if (source.Length < 50)
            return (false);
        if (!source
            .Select(char.ToLowerInvariant)
            .All(c => 'a' <= c && c <= 'z' ||
                      '0' <= c && c <= '9'))
            return (false);
            
        return (true);
    }
    public static bool IsCashuToken(this string source) =>
        (source.StartsWith("cashuA", StringComparison.Ordinal) || source.StartsWith("cashuB", StringComparison.Ordinal))
        && source.Length > 10;

    public static string ObfuscatePaymentRequest(this string source)
    {
        if (source.IsPaymentRequest())
            return ($"{String.Concat(source.Take(18))}...{String.Concat(source.TakeLast(12))}");
        else
            return (source);
    }
}