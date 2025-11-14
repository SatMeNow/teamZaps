using System.Text;
using Microsoft.Extensions.Logging;

namespace teamZaps.Sessions;

public static class MessageHelper
{
    public static async Task UpdatePinnedStatusAsync<TLogger>(
        SessionState session,
        ITelegramBotClient botClient,
        SessionWorkflowService workflowService,
        ILogger<TLogger> logger,
        CancellationToken cancellationToken)
    {
        if (!session.StatusMessageId.HasValue)
            return;

        try
        {
            var statusText = BuildStatusMessage(session, workflowService);
            var keyboard = workflowService.BuildSessionJoinKeyboard(session, 0);
            await botClient.EditMessageText(
                chatId: session.ChatId,
                messageId: session.StatusMessageId.Value,
                text: statusText,
                parseMode: ParseMode.Markdown,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && 
            (ex.Message.Contains("message to edit not found") || ex.Message.Contains("message can't be edited")))
        {
            // Message was deleted, recreate it
            logger.LogInformation("Status message deleted for chat {ChatId}, recreating...", session.ChatId);
            await RecreateStatusMessageAsync(session, botClient, workflowService, logger, cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
            // Message content is the same, this is fine
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update pinned status message for chat {ChatId}", session.ChatId);
        }
    }

    private static async Task RecreateStatusMessageAsync<TLogger>(
        SessionState session,
        ITelegramBotClient botClient,
        SessionWorkflowService workflowService,
        ILogger<TLogger> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = await SendPinnedStatusAsync(session, botClient, workflowService, cancellationToken);

            logger.LogInformation("Status message recreated for chat {ChatId}, new messageId: {MessageId}", 
                session.ChatId, message.MessageId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recreate status message for chat {ChatId}", session.ChatId);
            session.StatusMessageId = null; // Clear invalid message ID
        }
    }
    public static async Task<Message> SendPinnedStatusAsync(
        SessionState session,
        ITelegramBotClient botClient,
        SessionWorkflowService workflowService,
        CancellationToken cancellationToken)
    {
        var statusText = BuildStatusMessage(session, workflowService);
        var joinKeyboard = workflowService.BuildSessionJoinKeyboard(session, 0); // Generic keyboard
        var statusMessage = await botClient.SendMessage(session.ChatId,
            statusText,
            parseMode: ParseMode.Markdown,
            replyMarkup: joinKeyboard,
            cancellationToken: cancellationToken);

        session.StatusMessageId = statusMessage.MessageId;

        await botClient.PinChatMessage(session.ChatId, statusMessage.MessageId, cancellationToken: cancellationToken);

        return (statusMessage);
    }
    
    private static string BuildStatusMessage(SessionState session, SessionWorkflowService workflowService)
    {
        var sb = new StringBuilder();
        sb.AppendLine("📊 *Session Status*\n");
        sb.AppendLine($"Phase: *{session.Phase}*");
        sb.AppendLine($"Started: {session.StartedAt:yyyy-MM-dd HH:mm} UTC");

        if (session.ConfirmedPayments.Count > 0)
        {
            sb.AppendLine($"Total collected: *{workflowService.TotalSats(session)} sats*");
            sb.AppendLine($"Payments: *{session.ConfirmedPayments.Count}*");
        }

        if (session.Phase == SessionPhase.LotteryOpen)
        {
            sb.AppendLine($"Lottery entries: *{session.LotteryParticipants.Count}*");
            if (session.LotteryClosesAt.HasValue)
            {
                var remaining = session.LotteryClosesAt.Value - DateTimeOffset.UtcNow;
                sb.AppendLine($"Time remaining: *{remaining.TotalMinutes:F0} minutes*");
            }
        }

        if (!session.Participants.IsEmpty)
        {
            sb.AppendLine($"\n*{session.Participants.Count}* Participant(s):");
            foreach (var participant in session.Participants)
            {
                var p = $"• {participant.Value.DisplayName}";
                if (participant.Value.TotalPaidSats > 0)
                    p += $": *{participant.Value.TotalPaidSats} sats*";
                sb.AppendLine(p);
            }
        }
        
        if (session.WinnerUserId.HasValue)
        {
            var winner = session.Participants[session.WinnerUserId.Value];
            sb.AppendLine($"\n🏆 Winner: *{winner.DisplayName}*");
        }

        if (session.Phase == SessionPhase.WaitingForPayout && session.PayoutScheduledAt.HasValue)
        {
            sb.AppendLine($"\nPayout scheduled: *{session.PayoutScheduledAt:yyyy-MM-dd HH:mm} UTC*");
            sb.AppendLine($"Votes for early payout: *{session.PayoutVotes.Count}*");
        }

        return sb.ToString();
    }
}
