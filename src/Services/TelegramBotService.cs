using teamZaps.Handlers;

namespace teamZaps.Services;

public class TelegramBotService : BackgroundService
{
    private readonly ILogger<TelegramBotService> _logger;
    private readonly ITelegramBotClient _botClient;
    private readonly UpdateHandler _updateHandler;

    public TelegramBotService(ILogger<TelegramBotService> logger, ITelegramBotClient botClient, UpdateHandler updateHandler)
    {
        _logger = logger;
        _botClient = botClient;
        _updateHandler = updateHandler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting Team Zaps Telegram Bot...");

            User me = await _botClient.GetMe(stoppingToken);
            _logger.LogInformation("Bot started successfully: @{BotUsername}", me.Username);

            var receiverOptions = new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() };
            await _botClient.ReceiveAsync(_updateHandler, receiverOptions, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting bot");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Team Zaps Telegram Bot...");
        await base.StopAsync(cancellationToken);
    }
}
