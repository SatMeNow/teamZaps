using teamZaps.Handlers;

namespace teamZaps.Services;

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
