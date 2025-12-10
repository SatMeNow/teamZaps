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

        var session = sessionManager.GetSessionByUser(userId);
        if (session is null)
        {
            await botClient.SendMessage(message.Chat.Id,
                "Use `/help` for commands.",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);
        }
        else
        {
            switch (session.Phase)
            {
                case SessionPhase.WaitingForLotteryParticipants:
                    await botClient.SendMessage(message.Chat.Id,
                        "⚠️ Payments are blocked until someone enters the lottery!\n\n" +
                        "Use the 🎰 Enter Lottery button in your welcome message or ask someone to enter the lottery first.",
                        cancellationToken: cancellationToken);
                    return;

                case SessionPhase.AcceptingPayments:
                    // Try to parse as payment from session participant
                    if (PaymentParser.TryParse(text, out var tokens, out var error))
                    {
                        await ProcessPrivatePaymentAsync(botClient, session, userId, displayName, tokens, text, cancellationToken);
                        return;
                    }
                    break;

                case SessionPhase.WaitingForInvoice:
                    // Check if this user is a winner waiting to submit an invoice
                    var winnerUser = session.GetWinnerUser(userId);
                    if (winnerUser?.SubmittedInvoice == false)
                    {
                        // This looks like an invoice submission
                        if (text.IsLightningInvoice(out var invoice))
                        {
                            await ProcessWinnerInvoiceAsync(botClient, session, winnerUser, invoice, cancellationToken);
                            return;
                        }
                    }
                    break;

                default:
                    await botClient.SendMessage(message.Chat.Id,
                        $"⚠️ Payments are not available in current session phase: {session.Phase}",
                        cancellationToken: cancellationToken);
                    return;
            }
            
            // Not a payment or invoice
            await botClient.SendMessage(message.Chat.Id,
                "Sorry, push the `payment` button for instructions or use `/help` for commands.",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);
        }
    }
    private async Task ProcessPrivatePaymentAsync(ITelegramBotClient botClient, SessionState session, long userId, string displayName, List<PaymentToken> tokens, string inputExpression, CancellationToken cancellationToken)
    {
        try
        {
            var participant = session.Participants[userId];

            // Process each token and create invoices
            foreach (var tokenGrp in tokens.GroupBy(t => t.Currency))
            {
                var grpCurrency = tokenGrp.Key;
                var unit = grpCurrency.ToUnitName();
                var memo = $"{session.ChatTitle}/{displayName} zapped";

                // Ensure invoice to be payed in Euro only
                if (grpCurrency != BotBehaviorOptions.AcceptedFiatCurrency)
                    throw new NotSupportedException($"Only {BotBehaviorOptions.AcceptedFiatCurrency.GetDescription()} payments are supported.");

                var grpAmount = (double)tokenGrp.Sum(tGrp => tGrp.Amount);
                var tipAmount = 0.0;
                if (participant.Tip > 0)
                    tipAmount = ((grpAmount * participant.Tip!.Value) / 100.0);
                var invoiceAmount = (grpAmount + tipAmount);

                // Check if this payment would exceed the sessions's remaining budget
                var totalFiatAmount = (session.FiatAmount + session.PendingPayments.Values
                    .Cast<ITipableAmount>()
                    .Sum(p => p.TotalFiatAmount));
                var remainingFiatAmount = (session.Budget - totalFiatAmount);
                if (invoiceAmount > remainingFiatAmount)
                    throw new InvalidOperationException($"💸 Payment rejected!\n\n" +
                        $"Your payment of {invoiceAmount.Format()} would exceed the session's total budget.\n\n" +
                        $"Available budget: {remainingFiatAmount.Format()}")
                        .AddLogLevel(LogLevel.Warning);

                try
                {
                    // TODO: pass the enum here, not a currency string! 
                    var invoice = await lnbitsService.CreateInvoiceAsync(invoiceAmount, unit, memo, cancellationToken).ConfigureAwait(false);
                    // Store as pending payment
                    var pending = new PendingPayment
                    {
                        PaymentHash = invoice!.PaymentHash,
                        PaymentRequest = invoice.PaymentRequest,
                        UserId = userId,
                        DisplayName = displayName,
                        Tokens = tokenGrp.ToArray(),
                        FiatAmount = grpAmount,
                        TipAmount = tipAmount,
                        Currency = grpCurrency,
                        CreatedAt = DateTimeOffset.UtcNow
                    };

                    session.PendingPayments.TryAdd(invoice.PaymentHash, pending);

                    var message = await PaymentMessage.SendAsync(pending, botClient, cancellationToken).ConfigureAwait(false);
                    pending.MessageId = message.MessageId;

                    logger.LogInformation("Created invoice for user {UserId} in session {ChatId}: {InvoiceAmount} {Currency}",
                        userId, session.ChatId, invoiceAmount, grpCurrency);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to create invoice for {invoiceAmount} {unit}.", ex);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating invoice for private payment");
            await botClient.SendException(userId, ex, cancellationToken);
        }
    }
    private async Task ProcessWinnerInvoiceAsync(ITelegramBotClient botClient, SessionState session, ParticipantState winnerUser, string bolt11, CancellationToken cancellationToken)
    {
        logger.LogInformation("Winner invoice submitted by {UserId} for session in chat {ChatId}", winnerUser.UserId, session.ChatId);

        var winnerInfo = session.Winners[winnerUser.UserId];

        // Decode and validate the invoice amount
        var decodedInvoice = await lnbitsService.DecodeInvoiceAsync(bolt11, cancellationToken);
        if (decodedInvoice is null)
        {
            await botClient.SendMessage(winnerUser.UserId, "❌ Invalid invoice! Please provide a valid Lightning invoice.", cancellationToken: cancellationToken);
            return;
        }

        // Validate invoice amount
        var expectedSats = winnerInfo.SatsAmount;
        var invoiceSats = decodedInvoice.Amount;
        if (invoiceSats != expectedSats)
        {
            await botClient.SendMessage(winnerUser.UserId,
                $"❌ Invoice amount mismatch!\n\n" +
                $"Expected: {winnerInfo.SatsAmount.Format()}\n" +
                $"Your invoice: {invoiceSats.Format()}\n" +
                "Please create a new invoice with the correct amount.",
                cancellationToken: cancellationToken);
            return;
        }

        winnerUser!.SubmittedInvoice = true;

        await botClient.SendMessage(winnerUser.UserId,
            "✅ Invoice received!\n⏳ Processing payout...",
            cancellationToken: cancellationToken);

        try
        {
            var paymentResult = await lnbitsService.PayInvoiceAsync(bolt11!, cancellationToken);
            if (paymentResult is not null)
            {
                if (session.PayoutCompleted)
                {
                    session.Phase = SessionPhase.Completed;

                    await WinnerMessage.UpdateAsync(session, PaymentStatus.Paid, paymentResult, botClient, workflowService, logger, cancellationToken);
                }

                // Update the pinned status message
                await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);
                
                // Update user status messages for all participants
                foreach (var participant in session.Participants.Values)
                {
                    await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken);
                }
                
                if (session.Phase.IsClosed())
                {
                    // Clean up session
                    workflowService.TryCloseSession(session.ChatId, false);

                    logger.LogInformation("Payout executed successfully by {UserId} for chat {ChatId}", winnerUser.UserId, session.ChatId);
                }
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
            logger.LogError(ex, "Error executing payout by {UserId} for chat {ChatId}", winnerUser.UserId, session.ChatId);
            await botClient.SendMessage(session.ChatId,
                "❌ Error during payout. Please contact support.",
                cancellationToken: cancellationToken);
        }
        
        await botClient.SendMessage(winnerUser.UserId,
            "✅ Payout completed.",
            cancellationToken: cancellationToken);
    }
}
