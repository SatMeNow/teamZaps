namespace teamZaps.Configuration;

public class BotBehaviorOptions
{
    /// <summary>
    /// If false, only chat administrators can start a session. Defaults to false.
    /// </summary>
    public bool AllowNonAdminSessionStart { get; set; } = false;

    /// <summary>
    /// If false, only chat administrators can stop a session and start the lottery.
    /// </summary>
    public bool AllowNonAdminSessionStop { get; set; } = false;

    /// <summary>
    /// If false, only chat administrators can force-close a session.
    /// </summary>
    public bool AllowNonAdminSessionClose { get; set; } = false;

    /// <summary>
    /// Duration during which participants can join the lottery after a session is stopped.
    /// </summary>
    public TimeSpan LotteryJoinWindow { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Delay between the winner submitting the invoice and the payout being executed.
    /// </summary>
    public TimeSpan PayoutDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Percentage (0-1) of payers required to fast-forward the payout by voting.
    /// </summary>
    public double EarlyPayoutVoteThreshold { get; set; } = 0.20d;

    /// <summary>
    /// Default fiat currency code used when parsing payments (e.g. EUR).
    /// </summary>
    public string DefaultFiatCurrency { get; set; } = "EUR";
}
