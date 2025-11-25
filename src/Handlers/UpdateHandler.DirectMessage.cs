using System.Text;
using teamZaps.Configuration;
using teamZaps.Services;
using teamZaps.Sessions;
using teamZaps.Utils;

namespace teamZaps.Handlers;

public partial class UpdateHandler
{
    private async Task HandleDirectMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var userId = message.From!.Id;
        var displayName = message.From.GetDisplayName();
        var text = message.Text?.Trim();

        if (string.IsNullOrEmpty(text))
            return;

        // Check if this user is a winner waiting to submit an invoice
        foreach (var session in sessionManager.ActiveSessions)
        {
            if (session.WinnerUserId == userId && 
                session.Phase == SessionPhase.WaitingForInvoice &&
                string.IsNullOrEmpty(session.WinnerInvoiceBolt11))
            {
                // This looks like an invoice submission
                if (text.IsLightningInvoice(out var invoice))
                {
                    await ProcessWinnerInvoiceAsync(botClient, session, userId, invoice, cancellationToken);
                    return;
                }
            }
        }

        // Try to parse as payment from session participant
        if (PaymentParser.TryParse(text, out var tokens, out var error))
        {
            // Find active session where this user is a participant
            var participantSession = sessionManager.ActiveSessions
                .FirstOrDefault(s => s.Participants.ContainsKey(userId));

            if (participantSession is not null)
            {
                if (participantSession.Phase == SessionPhase.WaitingForLotteryParticipants)
                {
                    await botClient.SendMessage(message.Chat.Id,
                        "⚠️ Payments are blocked until someone enters the lottery!\n\n" +
                        "Use the 🎰 Enter Lottery button in your welcome message or ask someone to enter the lottery first.",
                        cancellationToken: cancellationToken);
                    return;
                }
                else if (participantSession.Phase == SessionPhase.AcceptingPayments)
                {
                    await ProcessPrivatePaymentAsync(botClient, participantSession, userId, displayName, tokens, text, cancellationToken);
                    return;
                }
                else
                {
                    await botClient.SendMessage(message.Chat.Id,
                        $"⚠️ Payments are not available in current session phase: {participantSession.Phase}",
                        cancellationToken: cancellationToken);
                    return;
                }
            }
            else
            {
                await botClient.SendMessage(message.Chat.Id,
                    "❌ You're not part of any active session. Join a session in a group first!",
                    cancellationToken: cancellationToken);
                return;
            }
        }

        // Not a payment or invoice
        await botClient.SendMessage(message.Chat.Id,
            "Send me:\n" +
            "• Payment amounts (like `3,99`, `5,50eur`, `2eur+1000sat`) if you're in a session\n" +
            "• Lightning invoice (BOLT11) if you won the lottery\n" +
            "• Use /help for commands",
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }
    private async Task ProcessPrivatePaymentAsync(ITelegramBotClient botClient, SessionState session, long userId, string displayName, List<PaymentToken> tokens, string inputExpression, CancellationToken cancellationToken)
    {
        try
        {
            // Ensure invoice to be payed in Euro only
            if (tokens.Any(t => t.Currency != BotBehaviorOptions.AcceptedFiatCurrency))
                throw new NotSupportedException($"Only {BotBehaviorOptions.AcceptedFiatCurrency.GetDescription()} payments are supported.");

            // Process each token and create invoices
            foreach (var tokenGrp in tokens.GroupBy(t => t.Currency))
            {
                var grpAmount = (double)tokenGrp.Sum(tGrp => tGrp.Amount);
                var grpCurrency = tokenGrp.Key;
                var unit = grpCurrency.ToUnitName();
                var memo = $"{session.ChatTitle}/{displayName} zapped";

                try
                {
                    // TODO: pass the enum here, not a currency string! 
                    var invoice = await lnbitsService.CreateInvoiceAsync(grpAmount, unit, memo, cancellationToken).ConfigureAwait(false);
                    // Store as pending payment
                    var pending = new PendingPayment
                    {
                        PaymentHash = invoice!.PaymentHash,
                        PaymentRequest = invoice.PaymentRequest,
                        UserId = userId,
                        DisplayName = displayName,
                        Tokens = tokenGrp.ToArray(),
                        FiatAmount = grpAmount,
                        Currency = grpCurrency,
                        CreatedAt = DateTimeOffset.UtcNow
                    };

                    session.PendingPayments.TryAdd(invoice.PaymentHash, pending);

                    var message = await PaymentMessage.SendAsync(pending, botClient, cancellationToken).ConfigureAwait(false);
                    pending.MessageId = message.MessageId;

                    logger.LogInformation("Created invoice for user {UserId} in session {ChatId}: {Amount} {Currency}",
                        userId, session.ChatId, grpAmount, grpCurrency);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to create invoice for {grpAmount} {unit}.", ex);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating invoice for private payment");
            await botClient.SendMessage(userId, $"❌ {ex.Message}", cancellationToken: cancellationToken);
        }
    }
    private async Task ProcessWinnerInvoiceAsync(ITelegramBotClient botClient, SessionState session, long userId, string bolt11, CancellationToken cancellationToken)
    {
        logger.LogInformation("Winner invoice submitted for session in chat {ChatId}", session.ChatId);

        session.WinnerInvoiceBolt11 = bolt11;
        session.InvoiceSubmittedAt = DateTimeOffset.UtcNow;

        await botClient.SendMessage(userId,
            "✅ Invoice received!\n⏳ Processing payout...",
            cancellationToken: cancellationToken);

        // Execute payout
        await ExecutePayoutAsync(botClient, session, cancellationToken);
        
        await botClient.SendMessage(userId,
            "✅ Payout completed.",
            cancellationToken: cancellationToken);
    }
    private async Task ExecutePayoutAsync(ITelegramBotClient botClient, SessionState session, CancellationToken cancellationToken)
    {
        if (session.PayoutCompleted)
            return;

        try
        {
            var paymentResult = await lnbitsService.PayInvoiceAsync(session.WinnerInvoiceBolt11!, cancellationToken);
            
            if (paymentResult is not null)
            {
                session.Phase = SessionPhase.Closed;
                session.PayoutCompleted = true;
                session.PayoutExecutedAt = DateTimeOffset.UtcNow;

                await WinnerMessage.UpdateAsync(session, PaymentStatus.Paid, paymentResult, botClient, workflowService, logger, cancellationToken);

                // Update the pinned status message
                await StatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);
                
                // Clean up session
                workflowService.TryCloseSession(session.ChatId);

                logger.LogInformation("Payout executed successfully for chat {ChatId}", session.ChatId);
            }
            else
            {
                await botClient.SendMessage(session.ChatId,
                    "❌ Failed to execute payout. Please try again later.",
                    cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing payout for chat {ChatId}", session.ChatId);
            await botClient.SendMessage(session.ChatId,
                "❌ Error during payout. Please contact support.",
                cancellationToken: cancellationToken);
        }
    }
}
