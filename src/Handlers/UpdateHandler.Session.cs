using System.Diagnostics;
using System.Text;
using teamZaps.Configuration;
using teamZaps.Services;
using teamZaps.Sessions;
using teamZaps.Utils;

namespace teamZaps.Handlers;

public partial class UpdateHandler
{
    private async Task HandleStartSessionAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var userId = message.From!.Id;
        var displayName = message.From.GetDisplayName();

        // Check if request was done for a group chat:
        var chat = await botClient.GetChat(chatId);
        if (chat.Type != ChatType.Group && chat.Type != ChatType.Supergroup)
        {
            await botClient.SendMessage(chatId, 
                "❌ Sessions can only be started in group chats.",
                cancellationToken: cancellationToken);
            return;
        }

        // Check if only admins can start sessions
        if (!botBehaviour.AllowNonAdminSessionStart)
        {
            if (!await IsUserAdminAsync(botClient, chatId, userId, cancellationToken))
            {
                await botClient.SendMessage(chatId, 
                    "❌ Only group administrators can start a session.", 
                    cancellationToken: cancellationToken);
                return;
            }
        }

        if (workflowService.TryStartSession(chat, userId, displayName, out var session))
        {
            await SessionStatusMessage.SendAsync(session, botClient, workflowService, cancellationToken);
            logger.LogInformation("Session started in chat {ChatId} by user {UserId}", chatId, userId);
        }
        else
        {
            await botClient.SendMessage(chatId,
                "⚠️ A session is already active in this group!",
                cancellationToken: cancellationToken);
        }
    }
    private async Task HandleCloseSessionAsync(ITelegramBotClient botClient, long chatId, long userId, CancellationToken cancellationToken)
    {
        // Check permissions
        if (!botBehaviour.AllowNonAdminSessionClose)
        {
            if (!await IsUserAdminAsync(botClient, chatId, userId, cancellationToken))
            {
                await botClient.SendMessage(chatId,
                    "❌ Only group administrators can close a session.",
                    cancellationToken: cancellationToken);
                return;
            }
        }

        var session = workflowService.GetSessionByChat(chatId);
        if (session is null)
        {
            await botClient.SendMessage(chatId,
                "⚠️ No active session in this group.",
                cancellationToken: cancellationToken);
            return;
        }

        if (session.Phase > SessionPhase.AcceptingPayments)
        {
            await botClient.SendMessage(chatId,
                "⚠️ Session has already moved past the payment phase.",
                cancellationToken: cancellationToken);
            return;
        }

        if (!session.HasPayments)
        {
            await HandleCancelSessionAsync(botClient, chatId, userId, cancellationToken);
            return;
        }

        // Check if anyone entered the lottery
        if (session.LotteryParticipants.Count == 0)
        {
            await botClient.SendMessage(chatId,
                "❌ No one entered the lottery. Session cancelled.",
                cancellationToken: cancellationToken);

            workflowService.TryCloseSession(chatId, true);
        }
        else
        {
            // Draw winners based on budget limits
            SelectWinners(session);
            session.Phase = SessionPhase.WaitingForInvoice;

            await SessionSummaryMessage.SendAsync(session, botClient, logger, cancellationToken);
            
            await WinnerMessage.SendAsync(session, botClient, workflowService, cancellationToken);

            logger.LogInformation("Winners selected for chat {ChatId}: {Winners}", chatId, 
                string.Join(", ", session.Winners.Select(w => $"{w.Key} ({w.Value.FiatAmount.Format()})")));
        }
        
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);
        
        // Update user status messages for all participants
        foreach (var participant in session.Participants.Values)
        {
            await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken);
        }
    }
    private async Task HandleCancelSessionAsync(ITelegramBotClient botClient, long chatId, long userId, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByChat(chatId);
        if (session is null)
            return;

        // Check permissions
        if (session?.HasPayments == false)
            ; // Skip (No need to check)
        else if (!botBehaviour.AllowNonAdminSessionCancel)
        {
            if (!await IsUserAdminAsync(botClient, chatId, userId, cancellationToken))
            {
                await botClient.SendMessage(chatId,
                    "❌ Only group administrators can cancel a session.",
                    cancellationToken: cancellationToken);
                return;
            }
        }

        if (workflowService.TryCloseSession(chatId, true))
        {
            await SessionStatusMessage.UpdateAsync(session!, botClient, workflowService, logger, cancellationToken);
            
            // Update user status messages for all participants
            foreach (var participant in session!.Participants.Values)
            {
                await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken);
            }
        
            await botClient.SendMessage(chatId,
                "❌ Session has been cancelled and removed.",
                cancellationToken: cancellationToken);
            logger.LogInformation("Session cancelled in chat {ChatId} by user {UserId}", chatId, userId);
        }
        else
        {
            await botClient.SendMessage(chatId,
                "⚠️ No active session to close.",
                cancellationToken: cancellationToken);
        }   
    }
    private async Task HandleJoinSessionAsync(ITelegramBotClient botClient, long chatId, long userId, string displayName, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByChat(chatId);
        if (session is null || session.Phase > SessionPhase.AcceptingPayments)
        {
            await botClient.SendMessage(chatId, "⚠️ Session is not currently accepting new participants.", cancellationToken: cancellationToken);
            return;
        }

        // Check if user is already a participant in this session
        if (session.Participants.ContainsKey(userId))
        {
            await botClient.SendMessage(chatId,
                $"ℹ️ {displayName.ToMarkdownString()}, you're already part of this session!",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);
            return;
        }

        // Check if user is already participating in another session
        var existingSession = workflowService.GetSessionByUser(userId);
        if (existingSession is not null && existingSession.ChatId != chatId)
        {
            var existingChat = await botClient.GetChat(existingSession.ChatId, cancellationToken);
            await botClient.SendMessage(chatId,
                $"⚠️ {displayName.ToMarkdownString()}, you're already participating in a session in *{existingChat.Title}*!\n\n" +
                "You can only join one session at a time. Please complete your current session first.",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);
            return;
        }

        // Send private status message
        try
        {
            var participant = workflowService.EnsureParticipant(session, userId, displayName);
            await UserStatusMessage.SendAsync(session, participant, botClient, workflowService, logger, cancellationToken);
        }
        catch (Exception)
        {
            var warningMessage = await botClient.SendMessage(chatId,
                $"⚠️ {displayName.ToMarkdownString()}, please start a private chat with me first by clicking @{(await botClient.GetMe(cancellationToken)).Username}",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);
                
            // Mark user as pending session join with message ID for later deletion
            session.PendingJoins[userId] = (chatId, warningMessage.MessageId);

            return;
        }

        // Update the pinned status message
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);

        logger.LogInformation("User {UserId} joined session in chat {ChatId}", userId, chatId);
    }

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

    private async Task HandleJoinLotteryAsync(ITelegramBotClient botClient, long chatId, long userId, string displayName, double budget, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByUser(userId);
        if (session is null)
        {
            await botClient.SendMessage(chatId, "⚠️ No active session found.", cancellationToken: cancellationToken);
            return;
        }
        var participant = session.Participants[userId];

        // Check if user already joined
        if (session.LotteryParticipants.ContainsKey(userId))
        {
            await botClient.SendMessage(chatId,
                $"ℹ️ {participant.ToMarkdownUserName()}, you've already entered the lottery!",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);
            return;
        }

        // Check server-wide budget limit
        if (!CheckServerBudgetLimit(budget))
        {
            var availBudget = sessionManager.AvailableServerBudget!.Value;
            var minBudget = botBehaviour.BudgetChoices.Min();
            var message = $"⚠️💸 Sorry {participant.ToMarkdownUserName()}, your budget of {budget.Format()} would exceed the server-wide limit!\n\n" +
                $"Available at this time: {availBudget.Format()}\n\n";
            if (minBudget <= availBudget)
                message += $"Please choose a lower budget and try again.";
            else
                message += $"Currently, no budgets are available to join the lottery. Please try again later.";
            await botClient.SendMessage(chatId, message, cancellationToken: cancellationToken);
            return;
        }

        // Add user to lottery with budget
        session.LotteryParticipants[userId] = budget;

        // Handle first lottery participant - unlock payments
        if (session.Phase == SessionPhase.WaitingForLotteryParticipants)
        {
            session.Phase = SessionPhase.AcceptingPayments;
            logger.LogInformation("First lottery participant {UserId} in chat {ChatId}, payments unlocked", userId, chatId);
        }
        else
        {
            logger.LogInformation("User {UserId} joined lottery in chat {ChatId} with {Budget}€ budget", userId, chatId, budget);
        }
        
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);
        
        // Update user status messages for all participants
        foreach (var p in session.Participants.Values)
        {
            await UserStatusMessage.UpdateAsync(session, p, botClient, workflowService, logger, cancellationToken);
        }
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
        {
            await botClient.SendMessage(chatId,
                "ℹ️ No active session in this group.\n\nUse /startsession to start one!",
                cancellationToken: cancellationToken);
            return;
        }

        // Delete previous message (should exist, but we don't really know):
        if (session.StatusMessageId is not null)
            await botClient.DeleteMessage(chatId, session.StatusMessageId!.Value);

        await SessionStatusMessage.SendAsync(session, botClient, workflowService, cancellationToken);
    }


    #region Helper
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
