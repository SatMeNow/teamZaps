using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using teamZaps.Services;
using teamZaps.Utils;

namespace teamZaps.Sessions;

public static class MessageHelper
{
    #region Message.Status
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

        if (session.ConfirmedPayments.Count > 0)
        {
            sb.AppendLine($"💰 Total collected: *{workflowService.TotalSats(session)} sats*");
            sb.AppendLine($"Payments: *{session.ConfirmedPayments.Count}*");
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
        
        if (session.Phase == SessionPhase.WaitingForLotteryParticipants)
            sb.AppendLine("\n⚠️ **Payments are blocked** until someone enters the lottery first!");

        if (session.WinnerUserId.HasValue)
        {
            var winner = session.Participants[session.WinnerUserId.Value];
            sb.AppendLine($"\n🏆 Winner: *{winner.DisplayName}*");
        }

        return sb.ToString();
    }
    #endregion
    #region Message.Payment
    public static async Task<Message> SendPaymentMessageAsync(
        PendingPayment payment,
        ITelegramBotClient botClient,
        CancellationToken cancellationToken)
    {
        var messageText = BuildPaymentMessage(payment, PaymentStatus.Pending);
        var message = await botClient.SendMessage(
            chatId: payment.UserId,
            text: messageText,
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);

        return message;
    }
    public static async Task UpdatePaymentMessageAsync<TLogger>(
        PendingPayment payment,
        PaymentStatus status,
        ITelegramBotClient botClient,
        ILogger<TLogger> logger,
        CancellationToken cancellationToken)
    {
        if (!payment.MessageId.HasValue)
            return;

        try
        {
            var messageText = BuildPaymentMessage(payment, status);
            await botClient.EditMessageText(
                chatId: payment.UserId,
                messageId: payment.MessageId.Value,
                text: messageText,
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && 
            (ex.Message.Contains("message to edit not found") || ex.Message.Contains("message can't be edited")))
        {
            // Message was deleted, no need to recreate payment messages
            logger.LogInformation("Payment message deleted for user {UserId}, skipping update", payment.UserId);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
            // Message content is the same, this is fine
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update payment message for user {UserId}", payment.UserId);
        }
    }
    private static string BuildPaymentMessage(PendingPayment payment, PaymentStatus status)
    {
        return $"⚡ *Lightning invoice*\n\n" +
               $"Amount: `{payment.Amount} {payment.Currency}`\n" +
               $"Status: *{status}*\n\n" +
               $"`{payment.PaymentRequest}`\n\n" +
               $"{status.GetIcon()} {status.GetDescription()}";
    }
    #endregion
    #region Message.Winner
    public static async Task<Message> SendWinnerMessageAsync(
        SessionState session,
        ITelegramBotClient botClient,
        SessionWorkflowService workflowService,
        CancellationToken cancellationToken)
    {
        var messageText = BuildWinnerMessage(session, workflowService, PaymentStatus.Pending);
        var message = await botClient.SendMessage(
            chatId: session.ChatId,
            text: messageText,
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);

        session.WinnerMessageId = message.MessageId;
        return message;
    }

    public static async Task UpdateWinnerMessageAsync<TLogger>(
        SessionState session,
        PaymentStatus status,
        LnbitsPaymentResponse? paymentResult,
        ITelegramBotClient botClient,
        SessionWorkflowService workflowService,
        ILogger<TLogger> logger,
        CancellationToken cancellationToken)
    {
        if (!session.WinnerMessageId.HasValue)
            return;

        try
        {
            var messageText = BuildWinnerMessage(session, workflowService, status, paymentResult);
            await botClient.EditMessageText(
                chatId: session.ChatId,
                messageId: session.WinnerMessageId.Value,
                text: messageText,
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && 
            (ex.Message.Contains("message to edit not found") || ex.Message.Contains("message can't be edited")))
        {
            // Message was deleted, no need to recreate winner messages
            logger.LogInformation("Winner message deleted for chat {ChatId}, skipping update", session.ChatId);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
            // Message content is the same, this is fine
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update winner message for chat {ChatId}", session.ChatId);
        }
    }

    private static string BuildWinnerMessage(SessionState session, SessionWorkflowService workflowService, PaymentStatus status, LnbitsPaymentResponse? paymentResult = null)
    {
        if (!session.WinnerUserId.HasValue || !session.Participants.TryGetValue(session.WinnerUserId.Value, out var winner))
            throw new InvalidOperationException("Winner information not available");

        var totalSats = workflowService.TotalSats(session);
        switch (status)
        {
            case PaymentStatus.Pending:
                return ($"🎉🏆 *WINNER SELECTED!* 🏆🎉\n\n" +
                    $"Congratulations {winner.DisplayName}!\n\n" +
                    $"You won to pay fiat for *{totalSats} sats*!\n\n" +
                    $"⚡ Please create a *lightning invoice* for *{totalSats} sats* and send it to me in a private message.");
            case PaymentStatus.Paid:
                return ($"🎉🏆 *PAYOUT COMPLETED!* 🏆🎉\n\n" +
                    $"Congratulations {winner.DisplayName}!\n\n" +
                    $"Amount: *{paymentResult?.Amount ?? totalSats} sats*\n" +
                    (paymentResult != null ? $"Fee: *{paymentResult.Fee} sats*\n" : "") +
                    $"\nThank you for using Team Zaps! 🎉");

            default:
                throw new InvalidEnumArgumentException($"Invalid payment status '{status.GetDescription()}' for winner message");
        }
    }
    #endregion
}
