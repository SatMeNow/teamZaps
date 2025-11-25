using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using teamZaps.Configuration;
using teamZaps.Services;
using teamZaps.Utils;

namespace teamZaps.Sessions;


/// <summary>
/// Pinned status message in group-chat.
/// </summary>
internal static class StatusMessage
{
    public static async Task UpdateAsync<TLogger>(SessionState session, ITelegramBotClient botClient, SessionWorkflowService workflowService, ILogger<TLogger> logger, CancellationToken cancellationToken)
    {
        if (!session.StatusMessageId.HasValue)
            return;

        try
        {
            var statusText = Build(session, workflowService);
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
        
        if ((session.Phase == SessionPhase.Closed) && (session.StartMessageId is not null))
        {
            // Unpin status message
            await botClient.UnpinChatMessage(
                chatId: session.ChatId,
                messageId: session.StatusMessageId.Value,
                cancellationToken: cancellationToken);

            // Delete start message before closing session
            try
            {
                await botClient.DeleteMessage(session.ChatId, session.StartMessageId.Value, cancellationToken);
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
    public static async Task<Message> SendAsync(SessionState session, ITelegramBotClient botClient, SessionWorkflowService workflowService, CancellationToken cancellationToken)
    {
        var statusText = Build(session, workflowService);
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
    private static string Build(SessionState session, SessionWorkflowService workflowService)
    {
        var sb = new StringBuilder();
        sb.AppendLine("📊 *Session Status*\n");
        var phaseText = session.Phase switch
        {
            SessionPhase.WaitingForLotteryParticipants => "Waiting for Lottery Entries",
            SessionPhase.AcceptingPayments => "Accepting Payments",
            SessionPhase.WaitingForInvoice => "Waiting for Winner Invoice",
            SessionPhase.Closed => "Closed",
            _ => session.Phase.ToString()
        };
        sb.AppendLine($"Phase: *{phaseText}*");
        sb.AppendLine($"Started: {session.StartedAt:yyyy-MM-dd HH:mm} UTC");

        // Show lottery entries
        if (session.LotteryParticipants.Count > 0)
            sb.AppendLine($"🎟️ Lottery entries: *{session.LotteryParticipants.Count}*");

        if (session.HasPayments)
        {
            sb.AppendLine($"💰 Total: {session.FormatAmount()}");
            sb.AppendLine($"Payments: *{session.Payments.Count()}*");
        }

        if (!session.Participants.IsEmpty)
        {
            sb.AppendLine($"\n*{session.Participants.Count}* Participant(s):");
            foreach (var participant in session.Participants)
            {
                var p = $"• {participant.Value.DisplayName}";
                if (participant.Value.HasPayments)
                    p += $": {participant.Value.FormatAmount()}";
                sb.AppendLine(p);
            }
        }
        
        if (session.Phase == SessionPhase.WaitingForLotteryParticipants)
            sb.AppendLine("\n⚠️ **Payments are blocked** until someone enters the lottery first!");

        if (session.WinnerUserId.HasValue)
        {
            var winner = session.Participants[session.WinnerUserId.Value];
            sb.AppendLine($"\n🏆 Winner: *{winner.DisplayName}*");
        }

        return sb.ToString();
    }
}
