namespace TeamZaps.Configuration;

public class DebugSettings
{
    #region Constants
    public const string SectionName = "Debug";
    #endregion
    

#if DEBUG
    /// <summary>
    /// Pre-configured, fix budget for users when joining the lottery.
    /// </summary>
    public double? FixBudget { get; set; }
#endif
}
