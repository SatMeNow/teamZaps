using System.Text;
using TeamZaps.Backends;
using TeamZaps.Configuration;
using TeamZaps.Payment;
using TeamZaps.Session;
using TeamZaps.Utils;

namespace TeamZaps.Handlers;

internal static class LightningPaymentMessage
{
    public static Task<Message> SendAsync(PendingPayment payment, ITelegramBotClient botClient, CancellationToken cancellationToken) => botClient.SendMessage(
        chatId: payment.UserId,
        text: Build(payment, PaymentStatus.Pending),
        parseMode: ParseMode.Markdown,
        cancellationToken: cancellationToken);
    public static async Task UpdateAsync<TLogger>(PendingPayment payment, PaymentStatus status, ITelegramBotClient botClient, ILogger<TLogger> logger, CancellationToken cancellationToken)
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
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && 
            (ex.Message.Contains("message to edit not found") || ex.Message.Contains("message can't be edited")))
        {
            // Message was deleted, no need to recreate payment messages
            logger.LogInformation("Payment message deleted for user {User}, skipping update.", payment.User);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
            // Message content is the same, this is fine
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update payment message for user {User}.", payment.User);
        }
    }
    private static string Build(PendingPayment payment, PaymentStatus status)
    {
        var paymentReq = payment.PaymentRequest;
        if (status != PaymentStatus.Pending)
            paymentReq = paymentReq.ObfuscatePaymentRequest();
            
        var notes = payment.Tokens
            .Select(t => t.Note)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToArray();

            
            
        return (new StringBuilder()
            .AppendLine($"*{PaymentMethod.Lightning.Format()} invoice*")
            .AppendLine()
            .AppendLineIf("• Notes: *{0}*", !notes.IsEmpty(), string.Join(", ", notes))
            .AppendLine($"• Amount: {payment.FormatOrderedAmount()}")
            .AppendLine($"• Status: *{status}*")
            .AppendLine()
            .AppendLine($"`{paymentReq}`")
            .AppendLine()
            .AppendLine($"{status.GetIcon()} {status.GetDescription()}")
            .ToString());
    }
}

/// <summary>
/// Payment request message for Cashu eCash token payments (push-based, no invoice).
/// </summary>
internal static class CashuPaymentMessage
{
    public static Task<Message> SendAsync(PendingPayment payment, ITelegramBotClient botClient, CancellationToken cancellationToken) => botClient.SendMessage(
        chatId: payment.UserId,
        text: Build(payment, PaymentStatus.Pending),
        parseMode: ParseMode.Markdown,
        cancellationToken: cancellationToken);

    public static async Task UpdateAsync<TLogger>(PendingPayment payment, PaymentStatus status, ITelegramBotClient botClient, ILogger<TLogger> logger, CancellationToken cancellationToken)
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
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 &&
            (ex.Message.Contains("message to edit not found") || ex.Message.Contains("message can't be edited")))
        {
            logger.LogInformation("Cashu payment message deleted for user {User}, skipping update.", payment.User);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified")) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update Cashu payment message for user {User}.", payment.User);
        }
    }

    private static string Build(PendingPayment payment, PaymentStatus status)
    {
        var notes = payment.Tokens
            .Select(t => t.Note)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToArray();

        var sb = new StringBuilder()
            .AppendLine($"*{PaymentMethod.Cashu.Format()} payment*")
            .AppendLine()
            .AppendLineIf("• Notes: *{0}*", !notes.IsEmpty(), string.Join(", ", notes))
            .AppendLine($"• Amount: {payment.FormatOrderedAmount()}")
            .AppendLine($"• Status: *{status}*")
            .AppendLine();

        if (status == PaymentStatus.Pending)
            sb.AppendLine("Send me a `cashuA…` token for the exact amount above.");
        else
            sb.AppendLine($"{status.GetIcon()} {status.GetDescription()}");

        return sb.ToString();
    }
}
