namespace teamZaps.Configuration;

public class TelegramSettings
{
    public const string SectionName = "Telegram";
    
    public string BotToken { get; set; } = string.Empty;
    /// <summary>
    /// List of root user IDs who have elevated permissions.
    /// </summary>
    public long[] RootUsers { get; set; } = [];
}
