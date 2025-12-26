using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using teamZaps.Services;
using teamZaps.Backend;
using teamZaps.Utils;

namespace teamZaps.Session;

internal static class WinnerMessage
{
    public static async Task<Message> SendAsync(SessionState session, ITelegramBotClient botClient, SessionWorkflowService workflowService, CancellationToken cancellationToken)
    {
        var message = await botClient.SendMessage(
            chatId: session.ChatId,
            text: Build(session, workflowService, PaymentStatus.Pending),
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        session.WinnerMessageId = message.MessageId;
        return message;
    }
    public static async Task UpdateAsync<TLogger>(SessionState session, PaymentStatus status, IPaymentResponse? paymentResult, ITelegramBotClient botClient, SessionWorkflowService workflowService, ILogger<TLogger> logger, CancellationToken cancellationToken)
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
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && 
            (ex.Message.Contains("message to edit not found") || ex.Message.Contains("message can't be edited")))
        {
            // Message was deleted, no need to recreate winner messages
            logger.LogInformation("Winner message deleted for session {Session}, skipping update", session);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
            // Message content is the same, this is fine
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update winner message for session {Session}", session);
        }
    }
    private static string Build(SessionState session, SessionWorkflowService workflowService, PaymentStatus status, IPaymentResponse? paymentResult = null)
    {
        if (session.Winners.Count == 0)
            throw new InvalidOperationException("No winners available");

        var message = new StringBuilder();
        switch (status)
        {
            case PaymentStatus.Pending:
                if (session.Winners.Count == 1)
                {
                    message.AppendLine("🎉🏆 *WINNER SELECTED!* 🏆🎉\n");
                
                    message.AppendLine($"Congratulations {session.WinnerUser!.MarkdownDisplayName()}!\n");
                    message.Append("I sent you a message with the *payment summary* and a *lightning invoice*.");
                }
                else
                {
                    message.AppendLine("🎉🏆 *WINNERS SELECTED!* 🏆🎉\n");
                
                    message.AppendLine($"🎰 We have *{session.Winners.Count} winners* to share the cost:\n");
                    foreach (var winner in session.Winners)
                    {
                        var winnerUser = session.Participants[winner.Key];
                        message.AppendLine($"• {winnerUser.MarkdownDisplayName()}: *{winner.Value.FiatAmount.Format()}*");
                    }
                    message.AppendLine("\nI sent each winner a message with their *payment summary* and *lightning invoice*.");
                }
                break;

            case PaymentStatus.Paid:
                Debug.Assert(paymentResult is not null);    
                message.AppendLine("🎉🏆 *PAYOUT COMPLETED!* 🏆🎉\n");
                
                if (session.Winners.Count == 1)
                    message.AppendLine($"Congratulations {session.WinnerUser!.MarkdownDisplayName()}!\n");
                else
                    message.AppendLine("All winners have been paid!\n");
                
                message.Append("*Thank you* for using Team Zaps! 🎉");
                break;

            default:
                throw new InvalidEnumArgumentException($"Invalid payment status '{status.GetDescription()}' for winner message");
        }

        return (message.ToString());
    }
}
