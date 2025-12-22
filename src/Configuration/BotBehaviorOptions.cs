using teamZaps.Sessions;

namespace teamZaps.Configuration;

public class BotBehaviorOptions
{
    public const string SectionName = "BotBehavior";
    
    /// <summary>
    /// Accepted fiat currency used for payments.
    /// </summary>
    public const PaymentCurrency AcceptedFiatCurrency = PaymentCurrency.Euro;
    
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
