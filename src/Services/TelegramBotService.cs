using teamZaps.Configuration;
using teamZaps.Handlers;

namespace teamZaps.Services;

public class TelegramBotService : BackgroundService
{
    public TelegramBotService(ILogger<TelegramBotService> logger, IOptions<TelegramSettings> settings, UpdateHandler updateHandler, LnbitsService lnbitsService)
    {
        this.logger = logger;
        this.settings = settings.Value;
        this.updateHandler = updateHandler;
        this.lnbitsService = lnbitsService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Starting Team Zaps Telegram Bot...");

            if (string.IsNullOrWhiteSpace(settings.BotToken))
            {
                logger.LogError("Bot token is not configured. Please set the Telegram:BotToken in appsettings.json or environment variables.");
                return;
            }

            botClient = new TelegramBotClient(settings.BotToken);

            User me = await botClient.GetMe(stoppingToken);
            logger.LogInformation("Bot started successfully: @{BotUsername}", me.Username);

            ReceiverOptions receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };
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

    private ILogger<TelegramBotService> logger;
    private TelegramSettings settings;
    private UpdateHandler updateHandler;
    private LnbitsService lnbitsService;
    private ITelegramBotClient? botClient;
}
