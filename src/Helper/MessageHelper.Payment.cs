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
        var message = await botClient.SendMessage(
            chatId: payment.UserId,
            text: Build(payment, PaymentStatus.Pending),
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);

        return message;
    }
    public static async Task UpdateAsync<TLogger>( PendingPayment payment, PaymentStatus status, ITelegramBotClient botClient, ILogger<TLogger> logger, CancellationToken cancellationToken)
    {
        if (payment.MessageId is null)
            return;

        try
        {
            await botClient.EditMessageText(
                chatId: payment.UserId,
                messageId: payment.MessageId.Value,
                text: Build(payment, status),
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
        var paymentReq = payment.PaymentRequest;
        if (status == PaymentStatus.Paid)
            paymentReq = paymentReq.ObfuscatePaymentRequest();
            
        var notes = payment.Tokens
            .Select(t => t.Note)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToArray();
            
        var message = new StringBuilder();
        message.AppendLine("⚡ *Lightning invoice*");
        message.AppendLine();
        message.AppendLineIf("• Notes: *{0}*", !notes.IsEmpty(), string.Join(", ", notes));
        message.AppendLine($"• Amount: {payment.FormatTotalFiatAmount()}");
        message.AppendLine($"• Status: *{status}*");
        message.AppendLine();
        message.AppendLine($"`{paymentReq}`");
        message.AppendLine();
        message.AppendLine($"{status.GetIcon()} {status.GetDescription()}");
        
        return message.ToString();
    }
}
