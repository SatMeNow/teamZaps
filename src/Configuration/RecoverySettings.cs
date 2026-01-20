using System;

namespace TeamZaps.Configuration;

public class RecoverySettings
{
    #region Constants
    public const string SectionName = "Recovery";
    #endregion


    /// <summary>
    /// Enable or disable the recovery system for lost sats.
    /// </summary>
    public bool Enable { get; set; } = true;
    /// <summary>
    /// Time of day when the recovery scan should execute.
    /// </summary>
    public TimeSpan DailyScanTime { get; set; } = TimeSpan.FromHours(6);
}
