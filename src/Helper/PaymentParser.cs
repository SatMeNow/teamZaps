using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace teamZaps.Sessions;

public static class PaymentParser
{
    private static readonly Regex TokenRegex = new(
        pattern: @"(?<amount>[0-9]+(?:[\.,][0-9]+)?)\s*(?<currency>sat|sats|eur|€|usd|\$)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        if (matches.Count == 0)
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
            var currency = currencyRaw switch
            {
                // TODO: switch/case ersetzen durch attribute in `PaymentCurrency`
                "" => PaymentCurrency.Sat,
                "sat" or "sats" => PaymentCurrency.Sat,
                "eur" or "€" => PaymentCurrency.Eur,
                "usd" or "$" => PaymentCurrency.Usd,
                _ => PaymentCurrency.Unknown
            };

            if (currency == PaymentCurrency.Unknown)
            {
                error = $"Unsupported currency: {currencyRaw}";
                return false;
            }

            tokens.Add(new PaymentToken(amount, currency));
        }

        if (tokens.Count == 0)
        {
            error = "No payment amounts parsed.";
            return false;
        }

        return true;
    }
}

public enum PaymentCurrency
{
    [Description("sat")]
    Sat,
    [Description("EUR")]
    Eur,
    [Description("USD")]
    Usd,

    Unknown
}

public record PaymentToken(decimal Amount, PaymentCurrency Currency);
