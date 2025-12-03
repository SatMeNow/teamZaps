using System.Text;
using teamZaps.Services;
using teamZaps.Sessions;
using teamZaps.Utils;

namespace teamZaps.Handlers;

public partial class UpdateHandler : IUpdateHandler
{
    public UpdateHandler(ILogger<UpdateHandler> logger,  LnbitsService lnbitsService, SessionManager sessionManager, SessionWorkflowService workflowService)
    {
        this.logger = logger;
        this.lnbitsService = lnbitsService;
        this.sessionManager = sessionManager;
        this.workflowService = workflowService;
    }


    public Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            switch (update.Type)
            {
                case UpdateType.Message: return (HandleMessageAsync(botClient, update.Message!, cancellationToken));
                case UpdateType.CallbackQuery: return (HandleCallbackQueryAsync(botClient, update.CallbackQuery!, cancellationToken));
                case UpdateType.EditedMessage: return (HandleEditedMessageAsync(botClient, update.EditedMessage!, cancellationToken));

                default:
                    logger.LogInformation("Received update type: {UpdateType}", update.Type);
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

    private async Task HandleMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        logger.LogInformation("Received message from {ChatId}: {MessageText}", chatId, message.Text);

        // Only process group messages for session functionality
        if (message.Chat.Type != ChatType.Private && message.Chat.Type != ChatType.Channel)
        {
            if (message.IsCommand())
            {
                await HandleCommandAsync(botClient, message, cancellationToken);
            }
        }
        else if (message.IsCommand())
        {
            await HandleCommandAsync(botClient, message, cancellationToken);
        }
        else
        {
            // Check if this is an invoice submission in DM
            await HandleDirectMessageAsync(botClient, message, cancellationToken);
        }
    }
    private async Task HandleCommandAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var userId = message.From!.Id;
        message.GetCommand(out var cmd, out _);
        
        try
        {
            switch (cmd)
            {
                case "/start":
                    await botClient.SendMessage(chatId, 
                        "Welcome to Team Zaps! 🎯\n\n" +
                        "I help groups split bills using Bitcoin Lightning!\n\n" +
                        "*How it works:*\n" +
                        "1️⃣ Someone starts a session in your group\n" +
                        "2️⃣ Join the session using the _Join_ button\n" +
                        "3️⃣ Send me payments as direct message\n" +
                        "4️⃣ One random participant wins the pot!\n\n" +
                        "Use `/help` for detailed instructions.", 
                        parseMode: ParseMode.Markdown,
                        cancellationToken: cancellationToken);

                    // Check if user has a pending session join
                    foreach (var session in sessionManager.ActiveSessions)
                    {
                        if (session.PendingJoins.TryRemove(userId, out var joinInfo))
                        {
                            // Delete the warning message that told user to start bot chat
                            try
                            {
                                await botClient.DeleteMessage(joinInfo.ChatId, joinInfo.MessageId, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Failed to delete pending join message {MessageId} in chat {ChatId}", joinInfo.MessageId, joinInfo.ChatId);
                            }
                            
                            await HandleJoinSessionAsync(botClient, joinInfo.ChatId, userId, message.From.GetDisplayName(), cancellationToken);
                            break;
                        }
                    }

                    break;

                case "/help":
                    await botClient.SendMessage(chatId,
                        "🎯 *Team Zaps Help*\n\n" +
                        "*Commands:*\n" +
                        "/startsession - Start a new payment session\n" +
                        "/closesession - Close payments and start lottery\n" +
                        "/cancelsession - Cancel session (admin only)\n" +
                        "/status - View session details\n\n" +
                        "*How it works:*\n" +
                        "1️⃣ Join the session using the button on the pinned message\n" +
                        "2️⃣ Send me payment amounts in *private chat*\n" +
                        "3️⃣ Pay the Lightning invoices I send you\n" +
                        "4️⃣ Join the lottery when payments close!\n\n" +
                        "💡 *All payments happen in private messages for privacy!*",
                        parseMode: ParseMode.Markdown,
                        cancellationToken: cancellationToken);
                    break;

                case "/startsession":
                    await HandleStartSessionAsync(botClient, message, cancellationToken);
                    break;

                case "/closesession":
                    await HandleCloseSessionAsync(botClient, chatId, userId, cancellationToken);
                    break;

                case "/cancelsession":
                    await HandleCancelSessionAsync(botClient, chatId, userId, cancellationToken);
                    break;

                case "/status":
                    await HandleStatusAsync(botClient, chatId, cancellationToken);
                    break;

                default:
                    await botClient.SendMessage(chatId, 
                        "Unknown command. Use /help to see available commands.", 
                        cancellationToken: cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling command: {cmd}", cmd);
            await botClient.SendMessage(chatId, 
                "An error occurred while processing your command.", 
                cancellationToken: cancellationToken);
        }
    }
    private Task HandleEditedMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Received edited message from {ChatId}", message.Chat.Id);
        return Task.CompletedTask;
    }

    private async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        try
        {
            await botClient.AnswerCallbackQuery(query.Id, cancellationToken: cancellationToken);

            var data = query.Data;
            if (string.IsNullOrEmpty(data))
                return;

            var chatId = query.Message!.Chat.Id;
            var userId = query.From.Id;
            var displayName = query.From.GetDisplayName();

            switch (data)
            {
                case CallbackActions.JoinLottery:
                    await HandleJoinLotteryAsync(botClient, chatId, userId, displayName, query.Message.MessageId, cancellationToken);
                    break;

                case CallbackActions.ViewStatus:
                    await HandleStatusAsync(botClient, chatId, cancellationToken);
                    break;

                case CallbackActions.JoinSession:
                    await HandleJoinSessionAsync(botClient, chatId, userId, displayName, cancellationToken);
                    break;
                case CallbackActions.CloseSession:
                    await HandleCloseSessionAsync(botClient, chatId, userId, cancellationToken);
                    break;
                case CallbackActions.CancelSession:
                    await HandleCancelSessionAsync(botClient, chatId, userId, cancellationToken);
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
                            cancellationToken: cancellationToken);

                        participant.PaymentHelpMessageId = message.MessageId;
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling callback query");
        }
    }
    

    #region Helper
    private async Task<bool> IsUserAdminAsync(ITelegramBotClient botClient, long chatId, long userId, CancellationToken cancellationToken)
    {
        try
        {
            var member = await botClient.GetChatMember(chatId, userId, cancellationToken); // TODO: es fehlt oft `configureAwait(false)`...
            return member.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator;
        }
        catch
        {
            return false;
        }
    }
    #endregion


    private readonly ILogger<UpdateHandler> logger;
    private readonly LnbitsService lnbitsService;
    private readonly SessionManager sessionManager;
    private readonly SessionWorkflowService workflowService;
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
    public static void GetCommand(this Message source, out string command, out string[] args)
    {
        if (source?.IsCommand() == true)
        {
            var items = source.Text!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            {
                command = items.First().ToLower();
                args = items.Skip(1).ToArray();
            }
        }
        else
            throw new InvalidOperationException("Failed to parse command from message!");
    }
}
