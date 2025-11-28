using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using teamZaps.Configuration;
using teamZaps.Services;
using teamZaps.Utils;
using Telegram.Bot.Types.ReplyMarkups;

namespace teamZaps.Sessions;


/// <summary>
/// Pinned session status message in group-chat.
/// </summary>
internal static class SessionStatusMessage
{
    public static async Task<Message> SendAsync(SessionState session, ITelegramBotClient botClient, SessionWorkflowService workflowService, CancellationToken cancellationToken)
    {
        var statusMessage = await botClient.SendMessage(session.ChatId,
            text: Build(session),
            parseMode: ParseMode.Markdown,
            replyMarkup: BuildKeyboard(session, 0),
            cancellationToken: cancellationToken);

        session.StatusMessageId = statusMessage.MessageId;

        await botClient.PinChatMessage(session.ChatId, statusMessage.MessageId, cancellationToken: cancellationToken);

        return (statusMessage);
    }
    public static async Task UpdateAsync<TLogger>(SessionState session, ITelegramBotClient botClient, SessionWorkflowService workflowService, ILogger<TLogger> logger, CancellationToken cancellationToken)
    {
        if (session.StatusMessageId is null)
            return;

        try
        {
            await botClient.EditMessageText(
                chatId: session.ChatId,
                messageId: session.StatusMessageId.Value,
                text: Build(session),
                parseMode: ParseMode.Markdown,
                replyMarkup: BuildKeyboard(session, 0),
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && 
            (ex.Message.Contains("message to edit not found") || ex.Message.Contains("message can't be edited")))
        {
            // Message was deleted, recreate it
            logger.LogInformation("Status message deleted for chat {ChatId}, recreating...", session.ChatId);
            await RecreateAsync(session, botClient, workflowService, logger, cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
            // Message content is the same, this is fine
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update pinned status message for chat {ChatId}", session.ChatId);
        }
        
        if ((session.Phase == SessionPhase.Closed) && (session.StatusMessageId is not null))
        {
            // Unpin status message
            await botClient.UnpinChatMessage(
                chatId: session.ChatId,
                messageId: session.StatusMessageId.Value,
                cancellationToken: cancellationToken);

            // Delete start message before closing session
            try
            {
                await botClient.DeleteMessage(session.ChatId, session.StatusMessageId.Value, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete start message for cancelled session in chat {ChatId}", session.ChatId);
            }
        }
    }
    private static async Task RecreateAsync<TLogger>(SessionState session, ITelegramBotClient botClient, SessionWorkflowService workflowService, ILogger<TLogger> logger, CancellationToken cancellationToken)
    {
        try
        {
            var message = await SendAsync(session, botClient, workflowService, cancellationToken);

            logger.LogInformation("Status message recreated for chat {ChatId}, new messageId: {MessageId}", 
                session.ChatId, message.MessageId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recreate status message for chat {ChatId}", session.ChatId);
            session.StatusMessageId = null; // Clear invalid message ID
        }
    }
    private static string Build(SessionState session)
    {
        var status = new StringBuilder();
        
        status.AppendSessionState(session);

        // Show lottery entries
        if (session.LotteryParticipants.Count > 0)
            status.AppendLine($"🎫 Lottery entries: *{session.LotteryParticipants.Count}*");

        if (session.HasPayments)
        {
            status.AppendLine($"💰 Total: {session.FormatAmount()}");
            status.AppendLine($"Payments: *{session.Payments.Count()}*");
        }

        if (!session.Participants.IsEmpty)
        {
            status.AppendLine($"\n*{session.Participants.Count}* Participant(s):");
            foreach (var participant in session.Participants)
            {
                var p = $"• {participant.Value.DisplayName}";
                if (participant.Value.HasPayments)
                    p += $": {participant.Value.FormatAmount()}";
                status.AppendLine(p);
            }
        }
        
        if (session.Phase == SessionPhase.WaitingForLotteryParticipants)
        {
            status.AppendLine($"\n🎯 *New session* started by {session.StartedByUser}!");
            status.AppendLine("\n⚠️ *Payments are blocked* until someone enters the lottery first!");
        }

        if (session.WinnerUserId is not null)
        {
            var winner = session.Participants[session.WinnerUserId.Value];
            status.AppendLine($"\n🏆 Winner: *{winner.DisplayName}*");
        }

        return status.ToString();
    }

    private static InlineKeyboardMarkup? BuildKeyboard(SessionState session, long userId)
    {
        if (session.Phase == SessionPhase.Closed)
            return null;
        else
        {
            bool alreadyJoined = session.Participants.ContainsKey(userId);
            var joinButton = InlineKeyboardButton.WithCallbackData(alreadyJoined ? "✅ Joined" : "🎯 Join", CallbackActions.JoinSession);
            InlineKeyboardButton closeButton;
            if (session.HasPayments)
                closeButton = InlineKeyboardButton.WithCallbackData("🏆 Close", CallbackActions.CloseSession);
            else
                closeButton = InlineKeyboardButton.WithCallbackData("❌ Cancel", CallbackActions.CancelSession);
            return new InlineKeyboardMarkup(new[] { joinButton, closeButton });
        }
    }
}

/// <summary>
/// Pinned status message in a user's bot-chat.
/// Also used as welcome message.
/// </summary>
internal static class UserStatusMessage
{
    public static async Task SendAsync(SessionState session, long userId, string displayName, ITelegramBotClient botClient, SessionWorkflowService workflowService, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var chat = await botClient.GetChat(session.ChatId, cancellationToken);
            var message = await botClient.SendMessage(userId,
                text: Build(session, null, chat.Title ?? "Unknown Chat"),
                parseMode: ParseMode.Markdown,
                replyMarkup: BuildKeyboard(session),
                cancellationToken: cancellationToken);

            await botClient.PinChatMessage(message.Chat.Id, message.MessageId, cancellationToken: cancellationToken);
                
            // Add user as participant and store message ID
            var participant = workflowService.EnsureParticipant(session, userId, displayName);
            participant.StatusMessageId = message.MessageId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send private status message to user {UserId}", userId);
            throw; // Re-throw to allow caller to handle fallback
        }
    }
    public static async Task UpdateAsync(SessionState session, long userId, ITelegramBotClient botClient, SessionWorkflowService workflowService, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
    {
        if (!session.Participants.TryGetValue(userId, out var participant) || participant.StatusMessageId is null)
            return;
        
        var messageId = participant.StatusMessageId.Value;

        try
        {
            var chat = await botClient.GetChat(session.ChatId, cancellationToken);
            await botClient.EditMessageText(
                chatId: userId,
                messageId: messageId,
                text: Build(session, participant, chat.Title ?? "Unknown Chat"),
                parseMode: ParseMode.Markdown,
                replyMarkup: BuildKeyboard(session),
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && 
            (ex.Message.Contains("message to edit not found") || ex.Message.Contains("message can't be edited")))
        {
            // Message was deleted, recreate it
            logger.LogInformation("User status message deleted for user {UserId}, recreating...", userId);
            await RecreateAsync(session, userId, participant.DisplayName, botClient, workflowService, logger, cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
            // Message content is the same, this is fine
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update user status message for user {UserId}", userId);
        }
    }
    private static async Task RecreateAsync(SessionState session, long userId, string displayName, ITelegramBotClient botClient, SessionWorkflowService workflowService, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(session, userId, displayName, botClient, workflowService, logger, cancellationToken);

            logger.LogInformation("User status message recreated for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recreate user status message for user {UserId}", userId);
            // Clear invalid message ID if participant exists
            if (session.Participants.TryGetValue(userId, out var participant))
                participant.StatusMessageId = null;
        }
    }
    private static string Build(SessionState session, ParticipantState? participant, string chatTitle)
    {
        var status = new StringBuilder();
        status.AppendLine($"🎉 Welcome to the *{chatTitle}* Team Zaps session!\n");

        status.AppendSessionState(session);
        if (participant is not null)
            status.AppendLineIf("Lottery: *{0}*", participant.JoinedLottery, "🎫 Joined", "🎟️ Not joined");
        status.AppendLine();

        if (session.Phase == SessionPhase.WaitingForLotteryParticipants)
        {
            status.AppendLine("🎰 Feel free to *enter the lottery* if you're willing to pay the fiat bill if you win. In return, you'll receive all the sats collected from everyone!\n");
            status.AppendLine("⚠️ *Payments are blocked* until someone enters the lottery first!");
        }
        else if (session.Phase == SessionPhase.AcceptingPayments)
        {
            status.AppendLine("*Make payments* by sending amounts like:");
            status.AppendLine("• `3,99` (€ per default)");
            status.AppendLine("• `5,50eur` or `5€`");
            status.AppendLine("• `2eur+1000sat`");
            status.AppendLine("I'll create Lightning invoices for you to pay.");
        }

        if ((session.Phase >= SessionPhase.AcceptingPayments) && (participant?.HasPayments == true))
        {
            status.AppendLine("*Payments:*");
            status.AppendPayments(participant.Payments);
        }
        
        return status.ToString();
    }


    private static InlineKeyboardMarkup? BuildKeyboard(SessionState session)
    {
        if (session.Phase == SessionPhase.WaitingForLotteryParticipants)
            return new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("🎰 Enter Lottery", CallbackActions.JoinLottery));
        else
            return null;
    }
}