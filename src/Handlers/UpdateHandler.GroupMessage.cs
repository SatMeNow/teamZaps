using System.Diagnostics;
using System.Text;
using teamZaps.Configuration;
using teamZaps.Helper;
using teamZaps.Services;
using teamZaps.Sessions;
using teamZaps.Utils;
using Telegram.Bot.Types.ReplyMarkups;

namespace teamZaps.Handlers;

public partial class UpdateHandler
{
    private async Task<bool> HandleGroupCommandAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        switch (command.Value)
        {
            case "/startsession":
                await HandleStartSessionAsync(botClient, command, cancellationToken);
                break;
            case "/closesession":
                await HandleCloseSessionAsync(botClient, command.ChatId, command.From, cancellationToken);
                break;
            case "/cancelsession":
                await HandleCancelSessionAsync(botClient, command.ChatId, command.From, cancellationToken);
                break;

            case "/status":
                await HandleStatusAsync(botClient, command.ChatId, cancellationToken);
                break;

            default: return (false);
        }
        return (true);
    }

    private async Task HandleStartSessionAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        // Check if request was done for a group chat:
        var chat = await botClient.GetChat(command.ChatId);
        if (chat.Type != ChatType.Group && chat.Type != ChatType.Supergroup)
            throw new InvalidOperationException("Sessions can only be started in group chats.");

        // Check if only admins can start sessions
        if ((!botBehaviour.AllowNonAdminSessionStart) && (!await IsUserAdminAsync(botClient, command.ChatId, command.From, cancellationToken)))
            throw new UnauthorizedAccessException("Only group administrators can start a session.");

        if (workflowService.TryStartSession(chat, command.From, out var session))
        {
            // Check if messages can be pinned
            session.BotCanPinMessages = await botClient.BotCanPinMessagesAsync(chat.Id, cancellationToken);
            
            await SessionStatusMessage.SendAsync(session, botClient, workflowService, cancellationToken);
        }
        else
            throw new InvalidOperationException("A session is already active in this group!")
                .AddLogLevel(LogLevel.Warning);
    }
    private async Task HandleCloseSessionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        // Check permissions
        if (!botBehaviour.AllowNonAdminSessionClose)
        {
            if (!await IsUserAdminAsync(botClient, chatId, user, cancellationToken))
                throw new UnauthorizedAccessException("Only group administrators can close a session.");
        }

        var session = workflowService.GetSessionByChat(chatId);
        if (session is null)
            throw new InvalidOperationException("No active session in this group.")
                .AddLogLevel(LogLevel.Warning);

        if (session.Phase > SessionPhase.AcceptingPayments)
            throw new InvalidOperationException("Session has already moved past the payment phase.")
                .AddLogLevel(LogLevel.Warning);

        if (!session.HasPayments)
        {
            await HandleCancelSessionAsync(botClient, chatId, user, cancellationToken);
            return;
        }

        // Check if anyone entered the lottery
        if (session.LotteryParticipants.Count == 0)
        {
            await botClient.SendMessage(chatId, "❌ No one entered the lottery. Session cancelled.", cancellationToken: cancellationToken);

            workflowService.TryCloseSession(chatId, true);
        }
        else
        {
            // Draw winners based on budget limits
            SelectWinners(session);
            session.Phase = SessionPhase.WaitingForInvoice;

            await SessionSummaryMessage.SendAsync(session, botClient, logger, cancellationToken);
            
            await WinnerMessage.SendAsync(session, botClient, workflowService, cancellationToken);

            logger.LogInformation("Winners selected for session {Session}: {Winners}", session, 
                string.Join(", ", session.Winners.Select(w => $"{w.Key} ({w.Value.FiatAmount.Format()})")));
        }
        
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);
        
        // Update user status messages for all participants
        foreach (var participant in session.Participants.Values)
            await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken);
    }
    private async Task HandleCancelSessionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByChat(chatId);
        if (session is null)
            return;

        // Check permissions
        if (session?.HasPayments == false)
            ; // Skip (No need to check)
        else if (!botBehaviour.AllowNonAdminSessionCancel)
        {
            if (!await IsUserAdminAsync(botClient, chatId, user, cancellationToken))
                throw new UnauthorizedAccessException("Only group administrators can cancel a session.");
        }

        if (workflowService.TryCloseSession(chatId, true))
        {
            await SessionStatusMessage.UpdateAsync(session!, botClient, workflowService, logger, cancellationToken);
            
            // Update user status messages for all participants
            foreach (var participant in session!.Participants.Values)
                await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken);
        
            await botClient.SendMessage(chatId, "❌ Session has been cancelled and removed.", cancellationToken: cancellationToken);
            logger.LogInformation("Session {Session} cancelled by user {User}", session, user);
        }
        else
            throw new InvalidOperationException("No active session to close.")
                .AddLogLevel(LogLevel.Warning);   
    }
    private async Task HandleJoinSessionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByChat(chatId);
        if (session is null || session.Phase > SessionPhase.AcceptingPayments)
            throw new InvalidOperationException("Session is not currently accepting new participants.")
                .AddLogLevel(LogLevel.Warning);

        // Check if user is already a participant in this session
        if (session.Participants.ContainsKey(user.Id))
            throw new InvalidOperationException($"You're already part of this session!");

        // Check for lost sats of this user
        var lostSats = await recoveryService.TryGetLostSatsAsync(user.Id);
        if (lostSats is not null)
        {
            var message = new StringBuilder()
                .AppendRecoveryMessage(lostSats)
                .AppendLine("\nℹ️ You need to *recover your lost sats before joining* a new session.")
                .ToString();
            throw new Exception(message)
                .AddLogLevel(LogLevel.None);
        }

        // Check if user is already participating in another session
        var existingSession = workflowService.GetSessionByUser(user.Id);
        if ((existingSession is not null) && (existingSession.ChatId != chatId))
        {
            var existingChat = await botClient.GetChat(existingSession.ChatId, cancellationToken);
            throw new InvalidOperationException($"You're already participating in a session in *{existingChat.Title}*!\n\n" +
                "You can only join one session at a time. Please complete your current session first.")
                .AddLogLevel(LogLevel.Warning);
        }

        // Send private status message
        try
        {
            var participant = workflowService.EnsureParticipant(session, user);
            await UserStatusMessage.SendAsync(session, participant, botClient, workflowService, logger, cancellationToken);
        }
        catch (Exception)
        {
            var botUser = await botClient.GetMe(cancellationToken);
            var warningMessage = await botClient.SendMessage(chatId,
                $"Hey @{user.Username}, we did not meet before ✌️\n" +
                "I'm a telegram bot, *helping you* and your friends *to coordinate lightning payments*.\n\n" +
                $"ℹ️ Please *start a private chat* to interact with me, by clicking @{botUser.Username}. See you soon 👍",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);
                
            // Mark user as pending session join with message ID for later deletion
            session.PendingJoins[user.Id] = (chatId, warningMessage.MessageId);

            logger.LogInformation("Invited new user {User} to a private bot chat.", user.DisplayName());
            return;
        }

        // Update the pinned status message
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);

        logger.LogInformation("User {User} joined session in chat {ChatId}", user, chatId);
    }

    private async Task HandleJoinLotteryAsync(ITelegramBotClient botClient, long chatId, User user, double budget, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByUser(user.Id);
        if (session is null)
            throw new InvalidOperationException("No active session found.")
                .AddLogLevel(LogLevel.Warning);
        var participant = session.Participants[user.Id];

        // Check if user already joined
        if (session.LotteryParticipants.ContainsKey(user.Id))
            throw new InvalidOperationException($"You've already entered the lottery!");

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
                .AddLogLevel(LogLevel.Warning);
        }

        // Add user to lottery with budget
        session.LotteryParticipants[user.Id] = budget;

        // Handle first lottery participant - unlock payments
        if (session.Phase == SessionPhase.WaitingForLotteryParticipants)
        {
            session.Phase = SessionPhase.AcceptingPayments;
            logger.LogInformation("First lottery participant {User} in chat {ChatId}, payments unlocked", user, chatId);
        }
        else
            logger.LogInformation("User {User} joined lottery in chat {ChatId} with {Budget} budget", user, chatId, budget.Format());
        
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);
        
        // Update user status messages for all participants
        foreach (var p in session.Participants.Values)
            await UserStatusMessage.UpdateAsync(session, p, botClient, workflowService, logger, cancellationToken);
    }
    private async Task HandleJoinLotteryAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByUser(user.Id);
        if (session is null)
            throw new InvalidOperationException("No active session found.")
                .AddLogLevel(LogLevel.Warning);
        var participant = session.Participants[user.Id];
        if (session.LotteryParticipants.ContainsKey(user.Id))
            throw new InvalidOperationException($"You've already entered the lottery!");

        if (await DeleteMessageAsync(botClient, chatId, participant.BudgetSelectionMessageId, cancellationToken))
            participant.BudgetSelectionMessageId = null;

        var keyboard = botBehaviour.BudgetChoices
            .Select(c => InlineKeyboardButton.WithCallbackData($"{c}{BotBehaviorOptions.AcceptedFiatCurrency.ToSymbol()}", $"{CallbackActions.SelectBudget}_{c}"))
            .Chunk(4)
            .ToArray();

        var budgetMessage = await botClient.SendMessage(chatId, 
            "🎰 *Enter Lottery* 🎰\n\n" +
            "How much are you willing to pay in fiat at maximum?\n\n" +
            "💡 *Multiple winners possible!* If total payments exceed your budget, " +
            "we'll select multiple winners to share the cost.", 
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);

        participant.BudgetSelectionMessageId = budgetMessage.MessageId;
    }
    private async Task HandleJoinLotteryWithBudgetAsync(ITelegramBotClient botClient, long chatId, User user, double budget, CancellationToken cancellationToken)
    {
        var userId = user.Id;
        if (workflowService.TryGetSessionByUser(userId, out var session))
        {
            var participant = session.Participants[userId];

            if (await DeleteMessageAsync(botClient, chatId, participant.BudgetSelectionMessageId, cancellationToken))
                participant.BudgetSelectionMessageId = null;
                
            // Now process the lottery join
            await HandleJoinLotteryAsync(botClient, chatId, user, budget, cancellationToken);
        }
    }

    private async Task HandleTipSelectionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        var userId = user.Id;
        var session = workflowService.GetSessionByUser(userId);
        if (session is null)
            throw new InvalidOperationException("No active session found.")
                .AddLogLevel(LogLevel.Warning);
        
        var keyboard = new InlineKeyboardMarkup(botBehaviour.TipChoices
            .Prepend((byte)0)
            .Select(t => InlineKeyboardButton.WithCallbackData(t.FormatTip(), $"{CallbackActions.SelectTip}_{t}"))
            .Chunk(4)
            .ToArray());

        var tipMessage = await botClient.SendMessage(chatId, 
            "🎩 *Setup Tip*\n\n" +
            "Choose your *tip percentage* to be added to each payment:\n\n",
            //"💭 Tips help support Lightning Network infrastructure and development!"
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);

        // Store the tip selection message ID for cleanup
        if (session.Participants.TryGetValue(userId, out var participant))
            participant.TipSelectionMessageId = tipMessage.MessageId;
    }

    private async Task HandleSetTipAsync(ITelegramBotClient botClient, long chatId, User user, int tip, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByUser(user.Id);
        if (session is null)
            throw new InvalidOperationException($"No active session found.")
                .AddLogLevel(LogLevel.Warning);
        var participant = session.Participants[user.Id];
        
        // Delete the tip selection message
        if (await DeleteMessageAsync(botClient, chatId, participant.TipSelectionMessageId, cancellationToken))
            participant.TipSelectionMessageId = null;

        // Set the user's tip
        participant.Tip = (tip == 0) ? null : (byte)tip;
        // Update the user's status message to reflect the new tip
        await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken);
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
            throw new InvalidOperationException("No active session in this group.\n\nUse /startsession to start one!");

        // Delete previous message (should exist, but we don't really know):
        if (session.StatusMessageId is not null)
            await botClient.DeleteMessage(chatId, session.StatusMessageId!.Value);

        await SessionStatusMessage.SendAsync(session, botClient, workflowService, cancellationToken);
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
    #endregion
}