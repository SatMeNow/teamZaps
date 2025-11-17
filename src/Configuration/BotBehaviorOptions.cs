namespace teamZaps.Configuration;

public class BotBehaviorOptions
{
    /// <summary>
    /// If false, only chat administrators can start a session. Defaults to false.
    /// </summary>
    public bool AllowNonAdminSessionStart { get; set; } = false;

    /// <summary>
    /// If false, only chat administrators can close a session and start the lottery.
    /// </summary>
    public bool AllowNonAdminSessionClose { get; set; } = false;

    /// <summary>
    /// If false, only chat administrators can cancel a session.
    /// </summary>
    public bool AllowNonAdminSessionCancel { get; set; } = false;

    /// <summary>
    /// Default fiat currency code used when parsing payments (e.g. EUR).
    /// </summary>
    public string DefaultFiatCurrency { get; set; } = "EUR";
}
