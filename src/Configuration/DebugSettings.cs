namespace teamZaps.Configuration;

public class DebugSettings
{
    public const string SectionName = "Debug";

#if DEBUG
    /// <summary>
    /// Pre-configured, fix budget for users when joining the lottery.
    /// </summary>
    public double? FixBudget { get; set; }
#endif
}
