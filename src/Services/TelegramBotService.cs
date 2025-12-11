using System.Diagnostics.CodeAnalysis;
using teamZaps.Handlers;

namespace teamZaps.Services;


record struct CommandMessage(Message Source, string Value, string[] Arguments)
{
    #region  Properties
    public long ChatId => Source.Chat.Id;
    public long UserId => Source.From!.Id;
    #endregion


    public override string ToString() => Value;
}

public class TelegramBotService : BackgroundService
{
    public TelegramBotService(ILogger<TelegramBotService> logger, ITelegramBotClient botClient, UpdateHandler updateHandler)
    {
        this.logger = logger;
        this.botClient = botClient;
        this.updateHandler = updateHandler;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            User me = await botClient.GetMe(stoppingToken);
            logger.LogInformation("Bot initialized successfully: @{BotUsername}", me.Username);

            var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };
            await botClient.ReceiveAsync(updateHandler, receiverOptions, stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error starting bot");
            throw;
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping Team Zaps Telegram Bot...");
        await base.StopAsync(cancellationToken);
    }


    private readonly ILogger<TelegramBotService> logger;
    private readonly ITelegramBotClient botClient;
    private readonly UpdateHandler updateHandler;
}

internal static partial class Ext
{
    public static string GetDisplayName(this User user)
    {
        if (!string.IsNullOrEmpty(user.Username))
            return $"@{user.Username}";
        
        var name = user.FirstName;
        if (!string.IsNullOrEmpty(user.LastName))
            name += $" {user.LastName}";
        
        return name;
    }

    public static bool IsCommand(this Message source) => (source.Text?.StartsWith('/') == true);
    public static bool TryGetCommand(this Message source, [NotNullWhen(true)] out CommandMessage? command)
    {
        if (source?.IsCommand() == true)
        {
            var items = source.Text!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            command = new CommandMessage(
                source,
                items.First().ToLower(),
                items.Skip(1).ToArray()
            );
        }
        else
            command = null;

        return (command is not null);
    }
}