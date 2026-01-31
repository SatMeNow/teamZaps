using System.ComponentModel;
using System.Numerics;
using TeamZaps.Backends;
using TeamZaps.Configuration;
using TeamZaps.Utils;

namespace TeamZaps;


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
    public CurrencyAttribute(string Symbol, string[] abbreviations)
    {
        this.Symbol = Symbol;
        this.Abbreviations = abbreviations;
    }

    public string Symbol { get; }
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
    [Description("Satoshis"), Currency("丰", [ "s", "sat", "sats" ])] // Alternative signs: ⓢ ₛ 𝕤 丰
    Sats,
    [Description("Bitcoin"), Currency("₿", [ "btc", "bitcoin" ])]
    Bitcoin,
    [Description("Euro"), Currency("€", [ "eur", "euro" ])]
    Euro,
    [Description("US Dollar"), Currency("$", [ "usd" ])]
    Dollar,

    [Description("Cent"), Currency("¢", [ "c", "cnt", "cent", "cents" ])]
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


public static partial class Common
{
    #region Constants
    public static readonly string DataPath = Path.Combine(AppContext.BaseDirectory, "data");
    public static readonly string LogPath = Path.Combine(DataPath, "logs");
    
    public static readonly string AcceptedFiatPerBitcoinSymbol = $"{BotBehaviorOptions.AcceptedFiatCurrency.ToSymbol()}/{PaymentCurrency.Bitcoin.ToSymbol()}";
    
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
    #endregion
}


public static partial class Extensions
{
    #region Constants
    private static readonly IReadOnlyDictionary<PaymentStatus, IconAttribute> IconMap = UtilEnum.GetCustomAttributes<PaymentStatus, IconAttribute>();
    private static readonly IReadOnlyDictionary<PaymentCurrency, CurrencyAttribute> CurrencyMap = UtilEnum.GetCustomAttributes<PaymentCurrency, CurrencyAttribute>();
    #endregion


    public static ulong ToHash(this long id)
    {
        unchecked
        {
            var hash = (ulong)id;
            hash = ((hash >> 16) ^ hash) * 0x45d9f3bUL;
            hash = ((hash >> 16) ^ hash) * 0x45d9f3bUL;
            hash = (hash >> 16) ^ hash;
            return (hash);
        }
    }

    public static string GetIcon(this PaymentStatus source) => (IconMap.TryGetValue(source, out var icon) ? icon.Icon : "");

    public static PaymentCurrency? ToCurrency(this string? source)
    {
        if (string.IsNullOrEmpty(source))
            // Return default currency:
            return (BotBehaviorOptions.AcceptedFiatCurrency);

        return (CurrencyMap.TryGetKeyOf(c => c.Equals(source), out var currency) ? currency : null);
    }
    public static string ToSymbol(this PaymentCurrency source) => (CurrencyMap.TryGetValue(source, out var attr) ? attr.Symbol : "");
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
    public static string FormatFiatRate(this double source) => $"{source:N2} {Common.AcceptedFiatPerBitcoinSymbol}"; // `1,234.56`
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
    public static string? Format(this ulong source) => Format(source, PaymentCurrency.Sats);
    public static string? Format(this long source) => Format(source, PaymentCurrency.Sats);
    public static string? Format(this double source) => Format(source, BotBehaviorOptions.AcceptedFiatCurrency);
    public static string? Format<T>(this INumber<T> source, PaymentCurrency currency, bool hideZero = true)
        where T : INumber<T>
    {
        if ((T.Zero.Equals(source)) && (hideZero))
            return (null);
        
        if (currency == PaymentCurrency.Sats)
            return ($"{source:N0}{currency.ToSymbol()}"); // `1,234`
        else
            return ($"{source:N2}{currency.ToSymbol()}"); // `1,234.56`
    }
    public static string Format(this BlockHeader source) => $"{source.FormatHeight()} ({source.LocalTime:g})"; // `31.10.2008 17:04`
    public static string FormatHeight(this BlockHeader source) => $"[{source.Height.ToString("N0")}](https://mempool.space/block/{source.Hash})";
    
    public static double ToMinutes(this int blocks) => (blocks * 10);
    public static double ToHours(this int blocks) => (blocks / 6.0);
    public static double ToDays(this int blocks) => (blocks.ToHours() / 24.0);
    public static double ToWeeks(this int blocks) => (blocks.ToDays() / 7.0);
    public static double ToMonths(this int blocks) => (blocks.ToDays() / 30.0);

    public static double MinutesToBlocks(this int minutes) => (minutes / 10.0);
    public static double HoursToBlocks(this int hours) => (hours * 6.0);
    public static double DaysToBlocks(this int days) => (days.HoursToBlocks() * 24.0);
    public static double WeeksToBlocks(this int weeks) => (weeks.DaysToBlocks() * 7.0);
    public static double MonthsToBlocks(this int months) => (months.DaysToBlocks() * 30.0);
    public static double ToBlocks(this TimeSpan duration) => ((int)(duration.TotalMinutes / 30.0)).MinutesToBlocks();
}

internal static partial class Ext
{
    /// <summary>
    /// Tags an exception as answer to the user.
    /// </summary>
    /// <remarks>
    /// No need to append callstack as it is intended to be shown to the user.
    /// </remarks>
    public static T AnswerUser<T>(this T source, long? chatId = null)
        where T : Exception
    {
        return (source.AddData<T>(nameof(AnswerUser), chatId));
    }
    public static bool IsUserAnswer(this Exception source, out long? chatId) => source.TryGetData(nameof(AnswerUser), out chatId);
    
    /// <inheritdoc cref="ExpireMessage{T}(T, TimeSpan?)"/>
    public static T ExpireMessage<T>(this T source, int expireAfter)
        where T : Exception
    {
        return (ExpireMessage<T>(source, TimeSpan.FromSeconds(expireAfter)));
    }
    /// <summary>
    /// Tags an exception to expire after a certain time.
    /// </summary>
    /// <remarks>
    /// Default is to expire after a short time.
    /// </remarks>
    /// <param name="expireAfter"></param>
    public static T ExpireMessage<T>(this T source, TimeSpan? expireAfter = null)
        where T : Exception
    {
        return (source.AddData<T>(nameof(ExpireMessage), expireAfter));
    }
    public static bool IsMessageExpiring(this Exception source, out TimeSpan? expireAfter) => source.TryGetData(nameof(ExpireMessage), out expireAfter);
}