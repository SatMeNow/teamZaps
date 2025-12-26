using System.Text;
using teamZaps.Configuration;
using teamZaps.Services;
using teamZaps.Backend;
using teamZaps.Session;
using teamZaps.Utils;

namespace teamZaps.Handlers;

public partial class UpdateHandler : IUpdateHandler
{
    public UpdateHandler(
        ILogger<UpdateHandler> logger, IHostEnvironment hostEnvironment,
        IOptions<BotBehaviorOptions> botBehaviour, IOptions<DebugSettings> debugSettings, IOptions<TelegramSettings> telegramSettings,
        FileService<BotAdminOptions> adminOptionsService, RecoveryService recoveryService,
        SessionManager sessionManager, SessionWorkflowService workflowService,
        IEnumerable<IBackend> backends)
    {
        this.logger = logger;
        this.hostEnvironment = hostEnvironment;

        this.botBehaviour = botBehaviour.Value;
        this.debugSettings = debugSettings.Value;
        this.telegramSettings = telegramSettings.Value;

        this.adminOptionsService = adminOptionsService;
        this.recoveryService = recoveryService;
        
        this.sessionManager = sessionManager;
        this.workflowService = workflowService;

        // Extract specific backend instances:
        this.indexerBackend = backends.GetMandatoryBackend<IIndexerBackend>();
        this.lightningBackend = backends.GetMandatoryBackend<ILightningBackend>();
        this.exchangeRateBackend = backends.GetOptionalBackend<IExchangeRateBackend>();
    }


    public Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            switch (update.Type)
            {
                case UpdateType.Message: return (HandleMessageAsync(botClient, update.Message!, cancellationToken));
                case UpdateType.CallbackQuery: return (HandleCallbackQueryAsync(botClient, update.CallbackQuery!, cancellationToken));

                default:
                    logger.LogInformation("Received update of not implemented type '{UpdateType}'", update.Type);
                    return (Task.CompletedTask);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling update");
            return (Task.FromException(ex));
        }
    }
    public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
    {
        string errorMessage;
        if (exception is ApiRequestException apiRequestException)
        {
            errorMessage = $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}";
        }
        else
        {
            errorMessage = exception.ToString();
        }

        logger.LogError("HandleError: {ErrorMessage}", errorMessage);
        return Task.CompletedTask;
    }

    private async Task<bool> HandleMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        #if DEBUG
        logger.LogInformation("Received message from {User} in chat {ChatId}: {MessageText}", message.From, chatId, message.Text);
        #endif
        
        var res = true;
        var isCmd = message.TryGetCommand(out var cmd);
        try
        {
            switch (message.Chat.Type)
            {
                case ChatType.Private:
                    if (isCmd)
                        res = await HandleDirectCommandAsync(botClient, cmd!.Value, cancellationToken).ConfigureAwait(false);
                    else
                        res = await HandleDirectMessageAsync(botClient, message, cancellationToken).ConfigureAwait(false);
                    break;

                case ChatType.Group:
                case ChatType.Supergroup:
                    if (isCmd)
                        res = await HandleGroupCommandAsync(botClient, cmd!.Value, cancellationToken).ConfigureAwait(false);
                    else
                        res = true; // Just ignore regular group messages.
                    break;
            }
            
            if (res)
                ; // Succeeded.
            else if (isCmd)
                throw new ArgumentException("Unknown command!");
            else
                throw new Exception("Unable to process message!");
        }
        catch (Exception ex)
        {
            res = false;
            if (isCmd)
                logger.LogError(ex, "Error handling command {Command}", cmd);
            else
                logger.LogError(ex, "Error handling message");

            await botClient.SendException(chatId, ex.AddHelp($"Use {BotPmCommand.Help} to see available commands."), cancellationToken).ConfigureAwait(false);
        }
        
        return (res);
    }

    private async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(query.Data))
            return;

        var chatId = query.Message!.Chat.Id;
        var userId = query.From.Id;

        var data = query.Data!.Split("_");
        var action = data.First();
        try
        {
            await botClient.AnswerCallbackQuery(query.Id, cancellationToken: cancellationToken).ConfigureAwait(false);

            switch (action)
            {
                case CallbackActions.JoinLottery:
                    #if DEBUG
                    // [Debug] Join lottery instant with fix budget
                    if (debugSettings.FixBudget is not null)
                    {
                        await HandleJoinLotteryWithBudgetAsync(botClient, chatId, query.From!, debugSettings.FixBudget.Value, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    #endif

                    await HandleJoinLotteryAsync(botClient, chatId, query.From!, cancellationToken).ConfigureAwait(false);
                    break;

                case CallbackActions.SelectBudget when (data.Length == 2):
                    var budget = double.Parse(data[1]);
                    await HandleJoinLotteryWithBudgetAsync(botClient, chatId, query.From!, budget, cancellationToken).ConfigureAwait(false);
                    break;

                case CallbackActions.ViewStatus:
                    await HandleStatusAsync(botClient, chatId, cancellationToken).ConfigureAwait(false);
                    break;

                case CallbackActions.JoinSession:
                    await HandleJoinSessionAsync(botClient, chatId, query.From!, cancellationToken).ConfigureAwait(false);
                    break;
                case CallbackActions.CloseSession:
                    await HandleCloseSessionAsync(botClient, chatId, query.From!, cancellationToken).ConfigureAwait(false);
                    break;
                case CallbackActions.CancelSession:
                    await HandleCancelSessionAsync(botClient, chatId, query.From!, cancellationToken).ConfigureAwait(false);
                    break;

                case CallbackActions.MakePayment:
                    var session = workflowService.GetSessionByUser(userId);
                    if (session is not null && session.Participants.TryGetValue(userId, out var participant))
                    {
                        var message = await botClient.SendMessage(chatId, 
                            "💰 To make payments, simply send me *euro denominated* amounts like:\n" +
                            "`3,99` or `5,50eur` or `5€`\n\n" +
                            "Add a *note* to improve your overview:\n" +
                            "`3,99 Beer`\n\n" +
                            "*Combine payments* with `+` or `newline`:\n" +
                            "`4,50 Pizza + 2,50 Water`\n\n" +
                            "⚡ I'll create Lightning invoices for you to pay!\n" +
                            "ℹ️ You can also send amounts without using the `payment` button.", 
                            parseMode: ParseMode.Markdown,
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        participant.PaymentHelpMessageId = message.MessageId;
                    }
                    break;
                case CallbackActions.SetTip:
                    await HandleTipSelectionAsync(botClient, chatId, query.From!, cancellationToken).ConfigureAwait(false);
                    break;
                case CallbackActions.SelectTip when (data.Length == 2):
                    var tip = int.Parse(data[1]);
                    await HandleSetTipAsync(botClient, chatId, query.From!, tip, cancellationToken).ConfigureAwait(false);
                    break;
                    
                case CallbackActions.AdminOptions:
                    await HandleSetOptionsAsync(botClient, query, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling callback query '{Action}'", action);

            await botClient.SendException(userId, ex, cancellationToken).ConfigureAwait(false);
        }
    }
    

    #region Helper
    private async Task<bool> DeleteMessageAsync(ITelegramBotClient botClient, long chatId, int? messageId, CancellationToken cancellationToken)
    {
        if (messageId is null)
            return (false);
            
        try
        {
            await botClient.DeleteMessage(chatId, messageId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete message {MessageId}", messageId);
        }
        return (true);
    }
    private Task<bool> IsUserAdminAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken) => IsUserAdminAsync(botClient, chatId, user.Id, cancellationToken);
    private async Task<bool> IsUserAdminAsync(ITelegramBotClient botClient, long chatId, long userId, CancellationToken cancellationToken)
    {
        try
        {
            var member = await botClient.GetChatMember(chatId, userId, cancellationToken).ConfigureAwait(false);
            return member.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator;
        }
        catch
        {
            return false;
        }
    }
    #endregion


    private readonly ILogger<UpdateHandler> logger;
    private readonly IHostEnvironment hostEnvironment;

    private readonly BotBehaviorOptions botBehaviour;
    private readonly DebugSettings debugSettings;
    private readonly TelegramSettings telegramSettings;

    private readonly FileService<BotAdminOptions> adminOptionsService;
    private readonly RecoveryService recoveryService;

    private readonly SessionManager sessionManager;
    private readonly SessionWorkflowService workflowService;

    private readonly IIndexerBackend indexerBackend;
    private readonly ILightningBackend lightningBackend;
    private readonly IExchangeRateBackend? exchangeRateBackend;
}

internal static partial class Ext
{
    public static Task SendException(this ITelegramBotClient source, User user, Exception exception, CancellationToken cancellationToken) => SendException(source, user.Id, exception, cancellationToken);
    public static Task SendException(this ITelegramBotClient source, long userId, Exception exception, CancellationToken cancellationToken)
    {
        var message = exception.Message;
        if (exception.Data.Contains("help"))
            message += $" {exception.Data["help"]}";
        if (exception.InnerException is not null)
            message += "\n\n" + string.Join("\n\n", exception.InnerException
                .Enumerate()
                .Select(ex => ex.Message));

        if (exception.Data.Contains(nameof(LogLevel)))
        {
            switch ((LogLevel)exception.Data[nameof(LogLevel)]!)
            {
                case LogLevel.None:
                    return Send(source, userId, null, message, cancellationToken);
                case LogLevel.Information:
                    return SendInfo(source, userId, message, cancellationToken);
                case LogLevel.Warning:
                    return SendWarning(source, userId, message, cancellationToken);
                case LogLevel.Error:
                case LogLevel.Critical:
                    break;

                default:
                    throw new NotSupportedException("Failed to send message due to not supported log level!");
            }
        }
        return SendError(source, userId, message, cancellationToken);
    }
    public static Task SendInfo(this ITelegramBotClient source, long userId, string message, CancellationToken cancellationToken) => Send(source, userId, "ℹ️", message, cancellationToken);
    public static Task SendWarning(this ITelegramBotClient source, long userId, string message, CancellationToken cancellationToken) => Send(source, userId, "⚠️", message, cancellationToken);
    public static Task SendError(this ITelegramBotClient source, long userId, string message, CancellationToken cancellationToken) => Send(source, userId, "❌", message, cancellationToken);
    private static Task Send(this ITelegramBotClient source, long userId, string? icon, string message, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(icon))
            message = $"{icon} {message}";
        return source.SendMessage(userId, message, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
    }
}

