namespace teamZaps.Configuration;

public class TelegramSettings
{
    public const string SectionName = "Telegram";
    
    public string BotToken { get; set; } = string.Empty;
}
