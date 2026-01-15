using System.Diagnostics;
using System.Text;
using TeamZaps.Configuration;
using TeamZaps.Helper;
using TeamZaps.Services;
using TeamZaps.Session;
using TeamZaps.Utils;
using Telegram.Bot.Types.ReplyMarkups;

namespace TeamZaps.Handlers;

public partial class UpdateHandler
{
    private async Task<bool> HandleGroupCommandAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        switch (command.Value)
        {
            case BotGroupCommand.StartSession:
                await HandleStartSessionAsync(botClient, command, cancellationToken).ConfigureAwait(false);
                break;
            case BotGroupCommand.CloseSession:
                await HandleCloseSessionAsync(botClient, command.ChatId, command.From, cancellationToken).ConfigureAwait(false);
                break;
            case BotGroupCommand.CancelSession:
                await HandleCancelSessionAsync(botClient, command.ChatId, command.From, cancellationToken).ConfigureAwait(false);
                break;

            case BotGroupCommand.Status:
                await HandleStatusAsync(botClient, command.ChatId, cancellationToken).ConfigureAwait(false);
                break;
            case BotGroupCommand.Statistics:
                var chatId = command.ChatId;
                var session = workflowService.GetSessionByChat(chatId);
                var adminOptions = await GetAdminOptions(session, chatId).ConfigureAwait(false);

                // Check permissions
                if ((!adminOptions.AllowNonAdminStatistics) && (!await IsUserAdminAsync(botClient, chatId, command.From, cancellationToken).ConfigureAwait(false)))
                    throw new InvalidOperationException("Only group administrators can view statistics.")
                        .AddLogLevel(LogLevel.Warning)
                        .AnswerUser();
                
                await GroupStatisticsMessage.SendAsync(botClient, statisticService, command.ChatId, cancellationToken).ConfigureAwait(false);
                break;
            case BotGroupCommand.Config:
                await HandleConfigCommandAsync(botClient, command, cancellationToken).ConfigureAwait(false);
                break;

            default: return (false);
        }
        return (true);
    }

    private async Task HandleStartSessionAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        var chat = await botClient.GetChat(command.ChatId).ConfigureAwait(false);
        if (chat.Type != ChatType.Group && chat.Type != ChatType.Supergroup)
            throw new InvalidOperationException("Sessions can only be started in group chats!")
                .AnswerUser();

        // Obtain block header at session start
        var currentBlock = await indexerBackend.GetCurrentBlockAsync(cancellationToken).ConfigureAwait(false);

        // Load admin options for this chat
        var adminOptions = await adminOptionsService.ReadAsync(command.ChatId).ConfigureAwait(false);

        // Check permissions - use saved options or default
        var allowNonAdminSessionStart = adminOptions?.AllowNonAdminSessionStart ?? false;
        if ((!allowNonAdminSessionStart) && (!await IsUserAdminAsync(botClient, command.ChatId, command.From, cancellationToken).ConfigureAwait(false)))
            throw new InvalidOperationException("Only group administrators can start a session.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();

        var session = await workflowService.TryStartSessionAsync(chat, command).ConfigureAwait(false);
        if (session is not null)
        {
            if (adminOptions is not null)
                session.AdminOptions = adminOptions;
            session.StartedAtBlock = currentBlock;
            session.BotCanPinMessages = await botClient.BotCanPinMessagesAsync(chat.Id, cancellationToken).ConfigureAwait(false);
            
            await SessionStatusMessage.SendAsync(session, botClient, workflowService, cancellationToken).ConfigureAwait(false);
        }
        else
            throw new InvalidOperationException("A session is already active in this group!")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();
    }
    private async Task HandleCloseSessionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByChat(chatId);
        if (session is null)
            throw new InvalidOperationException("No active session in this group.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();

        // Check permissions
        if ((!session.AdminOptions.AllowNonAdminSessionClose) && (!await IsUserAdminAsync(botClient, chatId, user, cancellationToken).ConfigureAwait(false)))
            throw new InvalidOperationException("Only group administrators can close a session.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();

        if (session.Phase > SessionPhase.AcceptingPayments)
            throw new InvalidOperationException("Session has already moved past the payment phase.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();

        if (!session.HasPayments)
        {
            await HandleCancelSessionAsync(botClient, chatId, user, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Check if anyone entered the lottery
        if (session.LotteryParticipants.Count == 0)
        {
            await botClient.SendMessage(chatId, "❌ No one entered the lottery. Session cancelled.", cancellationToken: cancellationToken).ConfigureAwait(false);

            workflowService.TryCloseSession(chatId, true);
        }
        else
        {
            // Draw winners based on budget limits
            SelectWinners(session);
            session.Phase = SessionPhase.WaitingForInvoice;

            await SessionSummaryMessage.SendAsync(botClient, logger, session, cancellationToken).ConfigureAwait(false);
            
            await WinnerMessage.SendAsync(session, botClient, workflowService, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Winners selected for session {Session}: {Winners}.", session, 
                string.Join(", ", session.Winners.Select(w => $"{w.Key} ({w.Value.FiatAmount.Format()})")));
        }
        
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        
        // Update user status messages for all participants
        foreach (var participant in session.Participants.Values)
            await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
    }
    private async Task HandleCancelSessionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByChat(chatId);
        if (session is null)
            return;

        // Check permissions
        if (session.HasPayments == false)
        {
            // Skip permission check for empty sessions
        }
        else if ((!session.AdminOptions.AllowNonAdminSessionCancel) && (!await IsUserAdminAsync(botClient, chatId, user, cancellationToken).ConfigureAwait(false)))
            throw new InvalidOperationException("Only group administrators can cancel a session.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();

        if (workflowService.TryCloseSession(chatId, true))
        {
            await SessionStatusMessage.UpdateAsync(session!, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
            
            // Update user status messages for all participants
            foreach (var participant in session!.Participants.Values)
                await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        
            await botClient.SendMessage(chatId, "❌ Session has been cancelled and removed.", cancellationToken: cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Session {Session} cancelled by user {User}.", session, user);
        }
        else
            throw new InvalidOperationException("No active session to close.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();   
    }
    private async Task HandleJoinSessionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByChat(chatId);
        if (session is null || session.Phase > SessionPhase.AcceptingPayments)
            throw new InvalidOperationException("Session is not currently accepting new participants.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();

        // Check if user is already a participant in this session
        if (session.Participants.ContainsKey(user.Id))
            throw new InvalidOperationException("You're already part of this session!")
                .AnswerUser();

        // Check for lost sats of this user
        var lostSats = await recoveryService.TryGetLostSatsAsync(user.Id).ConfigureAwait(false);
        if (lostSats is not null)
        {
            var message = new StringBuilder()
                .AppendRecoveryMessage(lostSats)
                .AppendLine("\nℹ️ You need to *recover your lost sats before joining* a new session.")
                .ToString();
            throw new Exception(message)
                .AddLogLevel(LogLevel.None)
                .AnswerUser();
        }

        // Check if user is already participating in another session
        var existingSession = workflowService.GetSessionByUser(user.Id);
        if ((existingSession is not null) && (existingSession.ChatId != chatId))
        {
            var existingChat = await botClient.GetChat(existingSession.ChatId, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"You're already participating in a session in *{existingChat.Title}*!\n\n" +
                "You can only join one session at a time. Please complete your current session first.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();
        }

        // Send private status message
        try
        {
            var participant = await workflowService.EnsureParticipantAsync(session, user).ConfigureAwait(false);
            await UserStatusMessage.SendAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            var botUser = await botClient.GetMe(cancellationToken).ConfigureAwait(false);
            var welcomeMessage = await botClient.SendMessage(chatId,
                $"Hey @{user.Username}, we did not meet before ✌️\n" +
                "I'm a telegram bot, *helping you* and your friends *to coordinate lightning payments*.\n\n" +
                $"ℹ️ Please *start a private chat* to interact with me, by clicking @{botUser.Username}. See you soon 👍",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Mark user as pending session join
            sessionManager.PendingJoins[user.Id] = new PendingJoinInfo(chatId, welcomeMessage.MessageId);
            // Remove user from participants to avoid inconsistencies
            session.Participants.Remove(user.Id, out _);

            logger.LogInformation("Invited new user {User} to a private bot chat.", user.DisplayName());
            return;
        }

        // Update the pinned status message
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("User {User} joined session in chat {ChatId}.", user, chatId);
    }

    private async Task HandleJoinLotteryAsync(ITelegramBotClient botClient, long chatId, User user, double budget, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByUser(user.Id);
        if (session is null)
            throw new InvalidOperationException("No active session found.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();
        var participant = session.Participants[user.Id];

        // Check if user already joined
        if (session.LotteryParticipants.ContainsKey(user.Id))
            throw new InvalidOperationException("You've already entered the lottery.")
                .AnswerUser();

        // Check server-wide budget limit
        if (!CheckServerBudgetLimit(budget))
        {
            var availBudget = sessionManager.AvailableServerBudget!.Value;
            var minBudget = botBehaviour.BudgetChoices.Min();
            var message = $"💸 Sorry, your budget of {budget.Format()} would exceed the server-wide limit!\n\n" +
                $"Available at this time: {availBudget.Format()}\n\n";
            if (minBudget <= availBudget)
                message += $"Please choose a lower budget and try again.";
            else
                message += $"Currently, no budgets are available to join the lottery. Please try again later.";
            throw new IndexOutOfRangeException(message)
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();
        }

        // Add user to lottery with budget
        session.LotteryParticipants[user.Id] = budget;

        // Handle first lottery participant - unlock payments
        if (session.Phase == SessionPhase.WaitingForLotteryParticipants)
        {
            session.Phase = SessionPhase.AcceptingPayments;
            logger.LogInformation("First lottery participant {User} in chat {ChatId}, payments unlocked.", user, chatId);
        }
        else
            logger.LogInformation("User {User} joined lottery in chat {ChatId} with {Budget} budget.", user, chatId, budget.Format());
        
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        
        // Update user status messages for all participants
        foreach (var p in session.Participants.Values)
            await UserStatusMessage.UpdateAsync(session, p, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
    }
    private async Task HandleJoinLotteryAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByUser(user.Id);
        if (session is null)
            throw new InvalidOperationException("No active session found.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();
        var participant = session.Participants[user.Id];
        if (session.LotteryParticipants.ContainsKey(user.Id))
            throw new InvalidOperationException("You've already entered the lottery!")
                .AnswerUser();

        if (await DeleteMessageAsync(botClient, chatId, participant.BudgetSelectionMessageId, cancellationToken).ConfigureAwait(false))
            participant.BudgetSelectionMessageId = null;

        var keyboard = botBehaviour.BudgetChoices
            .Select(c => InlineKeyboardButton.WithCallbackData($"{c}{BotBehaviorOptions.AcceptedFiatCurrency.ToSymbol()}", $"{CallbackActions.SelectBudget}_{c}"))
            .Chunk(4)
            .ToArray();

        var budgetMessage = await botClient.SendMessage(chatId, 
            "🎰 *Enter lottery* 🎰\n\n" +
            "How much are you willing to pay in fiat at maximum?\n\n" +
            "💡 *Multiple winners possible!* If total payments exceed your budget, " +
            "we'll select multiple winners to share the cost.", 
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        participant.BudgetSelectionMessageId = budgetMessage.MessageId;
    }
    private async Task HandleJoinLotteryWithBudgetAsync(ITelegramBotClient botClient, long chatId, User user, double budget, CancellationToken cancellationToken)
    {
        var userId = user.Id;
        if (workflowService.TryGetSessionByUser(userId, out var session))
        {
            var participant = session.Participants[userId];

            if (await DeleteMessageAsync(botClient, chatId, participant.BudgetSelectionMessageId, cancellationToken).ConfigureAwait(false))
                participant.BudgetSelectionMessageId = null;
                
            // Now process the lottery join
            await HandleJoinLotteryAsync(botClient, chatId, user, budget, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleTipSelectionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        var userId = user.Id;
        var session = workflowService.GetSessionByUser(userId);
        if (session is null)
            throw new InvalidOperationException("No active session found.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();
        if ((!session.AdminOptions.AllowNonAdminSessionClose) && (!await IsUserAdminAsync(botClient, chatId, user, cancellationToken).ConfigureAwait(false)))
            throw new InvalidOperationException("Only group administrators can close a session.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();        
        var keyboard = new InlineKeyboardMarkup(botBehaviour.TipChoices
            .Prepend((byte)0)
            .Select(t => InlineKeyboardButton.WithCallbackData(t.FormatTip(), $"{CallbackActions.SelectTip}_{t}"))
            .Chunk(4)
            .ToArray());

        var tipMessage = await botClient.SendMessage(chatId, 
            "🎩 *Setup tip*\n\n" +
            "Choose your *tip percentage* to be added to each payment:\n\n",
            //"💭 Tips help support Lightning Network infrastructure and development!"
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Store the tip selection message ID for cleanup
        if (session.Participants.TryGetValue(userId, out var participant))
            participant.TipSelectionMessageId = tipMessage.MessageId;
    }

    private async Task HandleSetTipAsync(ITelegramBotClient botClient, long chatId, User user, int tip, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByUser(user.Id);
        if (session is null)
            throw new InvalidOperationException($"No active session found.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();
        var participant = session.Participants[user.Id];
        
        // Delete the tip selection message
        if (await DeleteMessageAsync(botClient, chatId, participant.TipSelectionMessageId, cancellationToken).ConfigureAwait(false))
            participant.TipSelectionMessageId = null;

        // Set the user's tip:
        participant.Options.Tip = (tip == 0) ? null : (byte)tip;
        // Save user options:
        await userOptionsService.WriteAsync(user.Id, participant.Options).ConfigureAwait(false);
        
        // Update the user's status message to reflect the new tip:
        await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Views the current session status.
    /// </summary>
    /// <remarks>
    /// Maybe useful if the status message is far above in chat history and you want to move it down.
    /// </remarks>
    private async Task HandleStatusAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByChat(chatId);
        if (session is null)
            throw new InvalidOperationException($"No active session in this group.\n\nUse {BotGroupCommand.StartSession} to start one!")
                .AnswerUser();

        // Delete previous message (should exist, but we don't really know):
        if (session.StatusMessageId is not null)
            await botClient.DeleteMessage(chatId, session.StatusMessageId!.Value).ConfigureAwait(false);

        await SessionStatusMessage.SendAsync(session, botClient, workflowService, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleConfigCommandAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        var chatId = command.ChatId;
        var chatTitle = command.Source.Chat.Title!;
        var user = command.From;

        // Check if user is admin
        if (!await IsUserAdminAsync(botClient, chatId, user, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Only group administrators can change settings.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();

        var session = workflowService.GetSessionByChat(chatId);
        var options = await GetAdminOptions(session, chatId).ConfigureAwait(false);

        // Respond to user's private bot chat
        var message = await ConfigMessage.SendAsync(botClient, user.Id, chatTitle, options, cancellationToken).ConfigureAwait(false);
        
        // Store mapping from config message ID to group chat ID
        configMessageMap[message.MessageId] = chatId;

        // [Bug?] Fails to delete (Telegram responds `Bad request: message can't be deleted`)
        // await DeleteMessageAsync(botClient, chatId, command.Source.MessageId, cancellationToken).ConfigureAwait(false);
    }
    private async Task HandleSetOptionsAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        if (!configMessageMap.TryGetValue(query.Message!.MessageId, out var groupChatId))
            throw new InvalidOperationException($"Sorry, can't associate config message with group. Please run `{BotGroupCommand.Config}` again.")
                .AnswerUser();
            
        var chatId = query.Message!.Chat.Id;
        var chatTitle = query.Message!.Chat.Title!;

        var session = workflowService.GetSessionByChat(groupChatId);
        var options = await GetAdminOptions(session, groupChatId).ConfigureAwait(false);

        // Toggle the selected option
        var optionName = query.Data!.Split('_').Last();
        switch (optionName)
        {
            case "start": options.AllowNonAdminSessionStart = !options.AllowNonAdminSessionStart; break;
            case "close": options.AllowNonAdminSessionClose = !options.AllowNonAdminSessionClose; break;
            case "statistics": options.AllowNonAdminStatistics = !options.AllowNonAdminStatistics; break;
            case "cancel": options.AllowNonAdminSessionCancel = !options.AllowNonAdminSessionCancel; break;

            default: return;
        }

        // Save options
        await adminOptionsService.WriteAsync(groupChatId, options).ConfigureAwait(false);

        // Update session if active
        if (session is not null)
            session.AdminOptions = options;

        await ConfigMessage.UpdateAsync(botClient, chatId, query.Message.MessageId, chatTitle, options, cancellationToken).ConfigureAwait(false);

        await botClient.AnswerCallbackQuery(query.Id, "✅ Setting updated", cancellationToken: cancellationToken).ConfigureAwait(false);
    }


    #region Helper
    private int SelectWinners(SessionState session)
    {
        Debug.Assert(session.Winners.IsEmpty());

        var remainingAmount = ((ITipableAmount)session).TotalFiatAmount;

        // Shuffle participants for fair random selection
        var random = new Random();
        var lotteryParticipants = session.LotteryParticipants
            .OrderBy(_ => random.Next())
            .ToArray();

        foreach (var participant in lotteryParticipants)
        {
            if (remainingAmount <= 0)
                break;

            var userId = participant.Key;
            var budget = participant.Value;
            var amountToPay = Math.Min(budget, remainingAmount);
            var satsAmount = CalculateWinnerSats(session, amountToPay);
            
            session.Winners[userId] = new WinnerInfo(amountToPay, satsAmount);

            remainingAmount -= amountToPay;
        }

        return (session.Winners.Count);
    }

    private bool CheckServerBudgetLimit(double requestedBudget)
    {
        if (botBehaviour.MaxBudget is null)
            return (true);
        else
            return ((sessionManager.ConsumedServerBudget + requestedBudget) <= botBehaviour.MaxBudget.Value);
    }
    private static long CalculateWinnerSats(SessionState session, double fiatAmount)
    {
        // Don't use any exchange rate here!
        // > Just calculate proportionally to the total fiat and sats amounts.
        var totalFiat = ((ITipableAmount)session).TotalFiatAmount;
        var totalSats = session.SatsAmount;
        return (long)(totalSats * (fiatAmount / totalFiat));
    }

    private async Task<BotAdminOptions> GetAdminOptions(SessionState? session, long chatId)
    {
        BotAdminOptions? options = null;
        if (options is null)
            // Take options from active session:
            options = session?.AdminOptions;
        if (options is null)
            // Load saved options:
            options = await adminOptionsService.ReadAsync(chatId).ConfigureAwait(false);
        if (options is null)
            // Create new options:
            options = new BotAdminOptions();

        return (options);
    }
    #endregion

    
    private readonly Dictionary<int, long> configMessageMap = new(); // Config message ID -> group chat ID
}
