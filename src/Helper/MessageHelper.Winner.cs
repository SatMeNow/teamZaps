using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using teamZaps.Services;
using teamZaps.Utils;

namespace teamZaps.Sessions;

internal static class WinnerMessage
{
    public static async Task<Message> SendAsync(SessionState session, ITelegramBotClient botClient, SessionWorkflowService workflowService, CancellationToken cancellationToken)
    {
        var message = await botClient.SendMessage(
            chatId: session.ChatId,
            text: Build(session, workflowService, PaymentStatus.Pending),
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);

        session.WinnerMessageId = message.MessageId;
        return message;
    }
    public static async Task UpdateAsync<TLogger>(SessionState session, PaymentStatus status, LnbitsPaymentResponse? paymentResult, ITelegramBotClient botClient, SessionWorkflowService workflowService, ILogger<TLogger> logger, CancellationToken cancellationToken)
    {
        if (session.WinnerMessageId is null)
            return;

        try
        {
            await botClient.EditMessageText(
                chatId: session.ChatId,
                messageId: session.WinnerMessageId.Value,
                text: Build(session, workflowService, status, paymentResult),
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
    private static string Build(SessionState session, SessionWorkflowService workflowService, PaymentStatus status, LnbitsPaymentResponse? paymentResult = null)
    {
        if (session.WinnerUserId is null || !session.Participants.TryGetValue(session.WinnerUserId.Value, out var winner))
            throw new InvalidOperationException("Winner information not available");

        var message = new StringBuilder();
        switch (status)
        {
            case PaymentStatus.Pending:
                message.AppendLine("🎉🏆 *WINNER SELECTED!* 🏆🎉\n");
                message.AppendLine($"Congratulations {winner.DisplayName}!\n");
                message.Append("I sent you a message with the *payment summary* and a *lightning invoice*.");
                break;

            case PaymentStatus.Paid:
                Debug.Assert(paymentResult is not null);    
                var payed = (paymentResult!.Amount * -1);
                Debug.Assert(payed == session.SatsAmount);

                message.AppendLine("🎉🏆 *PAYOUT COMPLETED!* 🏆🎉\n");
                message.AppendLine($"Congratulations {winner.DisplayName}!\n");
                message.Append("*Thank you* for using Team Zaps! 🎉");
                break;

            default:
                throw new InvalidEnumArgumentException($"Invalid payment status '{status.GetDescription()}' for winner message");
        }

        return message.ToString();
    }
}
