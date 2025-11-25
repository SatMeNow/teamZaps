using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.Logging;
using teamZaps.Services;
using teamZaps.Utils;

namespace teamZaps.Sessions;

internal static class PaymentMessage
{
    public static async Task<Message> SendAsync(PendingPayment payment, ITelegramBotClient botClient, CancellationToken cancellationToken)
    {
        var messageText = Build(payment, PaymentStatus.Pending);
        var message = await botClient.SendMessage(
            chatId: payment.UserId,
            text: messageText,
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);

        return message;
    }
    public static async Task UpdateAsync<TLogger>( PendingPayment payment, PaymentStatus status, ITelegramBotClient botClient, ILogger<TLogger> logger, CancellationToken cancellationToken)
    {
        if (!payment.MessageId.HasValue)
            return;

        try
        {
            var messageText = Build(payment, status);
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
    private static string Build(PendingPayment payment, PaymentStatus status)
    {
        return $"⚡ *Lightning invoice*\n\n" +
               $"Amount: *{payment.FiatAmount.Format()}*\n" +
               $"Status: *{status}*\n\n" +
               $"`{payment.PaymentRequest}`\n\n" +
               $"{status.GetIcon()} {status.GetDescription()}";
    }
}
