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
    /// Budget choices, provided to the user for joining the lottery.
    /// </summary>
    public uint[] BudgetChoices { get; set; } = [ 50, 100, 150, 200, 250, 300, 350, 400 ];
}
