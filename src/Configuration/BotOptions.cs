using TeamZaps.Services;
using TeamZaps.Session;

namespace TeamZaps.Configuration;

[Storage("adminOpt", "chat_{0}.json")]
public class BotAdminOptions
{
    #region Properties
    /// <summary>
    /// If false, only chat administrators can start a session. Defaults to false.
    /// </summary>
    [JsonPropertyName("nonAdminSessionStart")]
    public bool AllowNonAdminSessionStart { get; set; } = true;
    /// <summary>
    /// If false, only chat administrators can close a session and start the lottery.
    /// </summary>
    [JsonPropertyName("nonAdminSessionClose")]
    public bool AllowNonAdminSessionClose { get; set; } = true;
    /// <summary>
    /// If false, only chat administrators can cancel a session.
    /// </summary>
    [JsonPropertyName("nonAdminSessionCancel")]
    public bool AllowNonAdminSessionCancel { get; set; } = true;
    /// <summary>
    /// If false, only chat administrators can view group statistics.
    /// </summary>
    [JsonPropertyName("nonAdminStatistics")]
    public bool AllowNonAdminStatistics { get; set; } = true;
    #endregion
}

[Storage("userOpt", "user_{0}.json")]
public class BotUserOptions
{
    #region Properties
    /// <summary>
    /// User's tip percentage preference.
    /// </summary>
    [JsonPropertyName("tip")]
    public byte? Tip { get; set; }
    #endregion
}

public class BotBehaviorOptions
{
    #region Constants
    public const string SectionName = "BotBehavior";
    #endregion

    
    /// <summary>
    /// Accepted fiat currency used for payments.
    /// </summary>
    public const PaymentCurrency AcceptedFiatCurrency = PaymentCurrency.Euro;
    /// <summary>
    /// Current locale/culture used system-wide for formatting and localization (e.g., "en-US", "de-DE", "it-IT").
    /// Defaults to invariant culture if not specified.
    /// </summary>
    public string? Locale { get; set; }

    /// <summary>
    /// Time of day (HH:mm:ss) to run backend sanity checks. Defaults to 03:00:00 if not specified.
    /// </summary>
    public TimeSpan SanityCheckTime { get; set; } = TimeSpan.FromHours(3);
    
    /// <summary>
    /// Tip (in [%]) choices, if the user wants to give a tip.
    /// </summary>
    public byte[] TipChoices { get; set; } = [ 1, 3, 5, 7, 10, 12, 15 ];
    /// <summary>
    /// Budget choices, provided to the user for joining the lottery.
    /// </summary>
    public uint[] BudgetChoices { get; set; } = [ 50, 100, 150, 200, 250, 300, 350, 400 ];
    /// <summary>
    /// Maximum total budget (in <see cref="AcceptedFiatCurrency">fiat</see>) across all active sessions server-wide.
    /// </summary>
    public double? MaxBudget { get; set; }
    /// <summary>
    /// Maximum total number of parallel sessions server-wide.
    /// </summary>
    public uint? MaxParallelSessions { get; set; }
}
