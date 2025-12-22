using teamZaps.Services;

namespace teamZaps.Configuration;


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
    #endregion
}