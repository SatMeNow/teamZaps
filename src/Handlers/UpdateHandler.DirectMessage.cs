using System.Diagnostics;
using System.Text;
using teamZaps;
using teamZaps.Configuration;
using teamZaps.Helper;
using teamZaps.Services;
using teamZaps.Sessions;
using teamZaps.Utils;
using Telegram.Bot.Types.ReplyMarkups;

namespace teamZaps.Handlers;

public partial class UpdateHandler
{
    private async Task<bool> HandleDirectCommandAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        switch (command.Value)
        {
            case "/start":
                await botClient.SendMessage(command.ChatId, 
                    "Welcome to Team Zaps! 🎯\n\n" +
                    "I help groups split bills using Bitcoin Lightning!\n\n" +
                    "*How it works:*\n" +
                    "1️⃣ Someone starts a session in your group\n" +
                    "2️⃣ Join the session using the _Join_ button\n" +
                    "3️⃣ Send me payments as direct message\n" +
                    "4️⃣ One random participant wins the pot!\n\n" +
                    "Use `/help` for detailed instructions.", 
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken);

                // Check if user has a pending session join
                foreach (var session in sessionManager.ActiveSessions)
                {
                    if (session.PendingJoins.TryRemove(command.UserId, out var joinInfo))
                    {
                        await DeleteMessageAsync(botClient, joinInfo.ChatId, joinInfo.MessageId, cancellationToken);
                        await HandleJoinSessionAsync(botClient, joinInfo.ChatId, command.Source.From!, cancellationToken);
                        break;
                    }
                }
                break;

            case "/help":
                await botClient.SendMessage(command.ChatId,
                    "🎯 *Team Zaps Help*\n\n" +
                    "*Group commands* (use in a group chat):\n" +
                    "/startsession - Start a new payment session (maybe for admins only)\n" +
                    "/closesession - Close payments and start lottery (maybe for admins only)\n" +
                    "/cancelsession - Cancel session (maybe for admins only)\n\n" +
                    "*Private commands* (use in direct message with the bot):\n" +
                    "/status - View session details (in group or private)\n" +
                    "/recover - Recover lost sats from interrupted sessions (private chat)\n" +
                    "/help - Show this help message\n\n" +
                    "*How to participate:*\n" +
                    "1️⃣ Join the session using the button on the status message in the group\n" +
                    "2️⃣ Send payment amounts here in *private chat*\n" +
                    "3️⃣ Pay the Lightning invoices I send you\n" +
                    "4️⃣ If you opted into the lottery, wait for the draw when the admin closes payments\n\n" +
                    "💡 *Payments and invoices are handled in private messages for privacy.*\n\n" +
                    "ℹ️ For *detailed info*, check out the [GitHub Repository](https://github.com/SatMeNow/teamZaps).",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken);
                break;

            case "/diag":
                await HandleDiagnosisAsync(botClient, command, cancellationToken);
                break;

            case "/recover":
                await HandleRecoverCommandAsync(botClient, command, cancellationToken);
                break;

            default: return (false);
        }
        return (true);
    }
    private async Task<bool> HandleDirectMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var user = message.From!;
        var text = message.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return (true);

        var session = sessionManager.GetSessionByUser(user.Id);
        if (session is null)
        {
            // Check if this is a recovery invoice
            if (text.IsLightningInvoice(out var recoveryInvoice))
            {
                var lostSats = await recoveryService.TryGetLostSatsAsync(user.Id);
                if (lostSats is not null)
                {
                    await ProcessRecoveryInvoiceAsync(botClient, user, recoveryInvoice, lostSats, cancellationToken);
                    return (true);
                }
            }
        }
        else
        {
            switch (session.Phase)
            {
                case SessionPhase.WaitingForLotteryParticipants:
                    throw new InvalidOperationException("Payments are blocked until someone enters the lottery!\n\n" +
                        "Use the 🎰 Enter Lottery button in your welcome message or ask someone to enter the lottery first.")
                        .AddLogLevel(LogLevel.Warning);

                case SessionPhase.AcceptingPayments:
                    // Try to parse as payment from session participant
                    if (PaymentParser.TryParse(text, out var tokens, out var error))
                    {
                        await ProcessPrivatePaymentAsync(botClient, session, user, tokens, text, cancellationToken);
                        return (true);   
                    }
                    break;

                case SessionPhase.WaitingForInvoice:
                    // Check if this user is a winner waiting to submit an invoice
                    var winnerUser = session.GetWinnerUser(user.Id);
                    if (winnerUser?.SubmittedInvoice == false)
                    {
                        // This looks like an invoice submission
                        if (text.IsLightningInvoice(out var invoice))
                        {
                            await ProcessWinnerInvoiceAsync(botClient, session, winnerUser, invoice, cancellationToken);
                            return (true);
                        }
                    }
                    break;

                default:
                    throw new InvalidOperationException($"Payments are not available in current session phase: {session.Phase}")
                        .AddLogLevel(LogLevel.Warning);
            }
        }
        
        throw new NotImplementedException("Sorry, push the `payment` button for instructions or use `/help` for commands.");
    }
    private async Task ProcessPrivatePaymentAsync(ITelegramBotClient botClient, SessionState session, User user, List<PaymentToken> tokens, string inputExpression, CancellationToken cancellationToken)
    {
        try
        {
            var participant = session.Participants[user.Id];
            // Process each token and create invoices
            foreach (var tokenGrp in tokens.GroupBy(t => t.Currency))
            {
                var grpCurrency = tokenGrp.Key;
                var memo = $"{session.ChatTitle}'{user} zapped";

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
                    var invoice = await lightningBackend.CreateInvoiceAsync(invoiceAmount, grpCurrency, memo, cancellationToken).ConfigureAwait(false);
                    // Store as pending payment
                    var pending = new PendingPayment
                    {
                        User = user,
                        PaymentHash = invoice!.PaymentHash,
                        PaymentRequest = invoice.PaymentRequest,
                        Tokens = tokenGrp.ToArray(),
                        SatsAmount = invoice.SatsAmount,
                        TipAmount = tipAmount,
                        FiatAmount = grpAmount,
                        Currency = grpCurrency,
                        CreatedAt = DateTimeOffset.Now
                    };

                    session.PendingPayments.TryAdd(invoice.PaymentHash, pending);

                    var message = await PaymentMessage.SendAsync(pending, botClient, cancellationToken).ConfigureAwait(false);
                    pending.MessageId = message.MessageId;

                    logger.LogInformation("Created invoice for user {User} in session {Session}: {InvoiceAmount}", user, session, invoiceAmount.Format(grpCurrency));
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to create invoice for {invoiceAmount.Format(grpCurrency)}.", ex);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating invoice for private payment");
            await botClient.SendException(user, ex, cancellationToken);
        }
    }
    private async Task ProcessWinnerInvoiceAsync(ITelegramBotClient botClient, SessionState session, ParticipantState winnerUser, string bolt11, CancellationToken cancellationToken)
    {
        logger.LogInformation("Winner invoice submitted by {User} for session {Session}", winnerUser, session);

        var winnerInfo = session.Winners[winnerUser.UserId];

        // Decode and validate the invoice amount
        var invoiceSats = lightningBackend.GetInvoiceAmount(bolt11);
        ValidateInvoiceAmount(winnerInfo.SatsAmount, invoiceSats);

        winnerUser!.SubmittedInvoice = true;

        await botClient.SendMessage(winnerUser.UserId, "✅ Invoice received!\n⏳ Processing payout...", cancellationToken: cancellationToken);

        try
        {
            var paymentResult = await lightningBackend.PayInvoiceAsync(bolt11!, cancellationToken);
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

                    logger.LogInformation("Payout executed successfully by {User} for session {Session}", winnerUser, session);
                }
            }
            else
                throw new InvalidOperationException("Failed to execute payout. Please try again later.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing payout by {User} for session {Session}", winnerUser, session);
            await botClient.SendMessage(session.ChatId, "❌ Error during payout. Please contact support.", cancellationToken: cancellationToken);
            return;
        }
        
        await botClient.SendMessage(winnerUser.UserId, "✅ Payout completed.", cancellationToken: cancellationToken);
    }
    /// <summary>
    /// Shows diagnostic information about the current session and system state.
    /// </summary>
    /// <remarks>
    /// Useful for debugging and monitoring session health.
    /// </remarks>
    private async Task HandleDiagnosisAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        // Check if user is a root user
        if (!telegramSettings.RootUsers.Contains(command.UserId))
            return;

        // Check if command is used in private chat
        var chat = await botClient.GetChat(command.ChatId, cancellationToken);
        if (chat.Type != ChatType.Private)
            throw new InvalidOperationException("This command is only available in private chat with the bot.");

        var diagnostics = new StringBuilder();
        diagnostics.AppendLine("🔍 *DIAGNOSTIC INFORMATION*");
        
        // Environment Information
        diagnostics.AppendLine("\n🖥️ *Environment:*");
        diagnostics.AppendLine($"• OS: *{Environment.OSVersion}*");
        diagnostics.AppendLine($"• .NET: *{Environment.Version}*");
        diagnostics.AppendLine($"• Host configuration: *{hostEnvironment.EnvironmentName}*");
        #if !DEBUG
        diagnostics.AppendLine($"• Timezone: *{TimeZoneInfo.Local.DisplayName}*");
        diagnostics.AppendLine($"• Machine: *{Environment.MachineName}*");
        diagnostics.AppendLine($"• User: *{Environment.UserName}*");
        #endif

        // Application Information
        var asmName = UtilAssembly.GetInfo();
        diagnostics.AppendLine("\n🚀 *Application:*");
        diagnostics.AppendLine($"• Version: *v{UtilAssembly.GetVersion()}*");
        diagnostics.AppendLine($"• Process ID: *{Environment.ProcessId}*");
        diagnostics.AppendLine($"• Is 64bit process: *{Environment.Is64BitProcess}*");
        diagnostics.AppendLine($"• Is privileged process: *{Environment.IsPrivilegedProcess}*");

        // System Information
        diagnostics.AppendLine("\n⚙️ *System status:*");
        diagnostics.AppendLine($"• CPU usage: *{Environment.CpuUsage.TotalTime}*");
        diagnostics.AppendLine($"• Memory usage: *{GC.GetTotalMemory(false) / 1024 / 1024:N0} MB*");
        diagnostics.AppendLine($"• Uptime: *{DateTimeOffset.Now - Process.GetCurrentProcess().StartTime:dd\\.hh\\:mm\\:ss}*");
        
        // Bot Information
        diagnostics.AppendLine("\n🤖 *Bot status:*");
        var maxSessions = "";
        if (botBehaviour.MaxParallelSessions > 0)
            maxSessions = $" of *{botBehaviour.MaxParallelSessions.Value}*";
        diagnostics.AppendLine($"• Active sessions: *{sessionManager.ActiveSessions.Count()}*{maxSessions}");

        if (botBehaviour.MaxBudget is null)
            diagnostics.AppendLine("• Server budget: *Unlimited*");
        else
        {
            var consumed = sessionManager.ConsumedServerBudget;
            if (consumed > 0)
                diagnostics.AppendLine($"• Server budget: *{consumed.Format()}* / *{botBehaviour.MaxBudget!.Value.Format()}*");
            var available = sessionManager.AvailableServerBudget ?? 0;
            if (available > 0)
                diagnostics.AppendLine($"• Available budget: *{available.Format()}*");
        }
        diagnostics.AppendLineIfNotNull("• Total locked amount: *{0}*", sessionManager.FormatAmount(), "💤 none");

        // Lost and Found Recovery Information
        diagnostics.AppendLine("\n🔍 *Lost and Found recovery:*");
        var allLostSats = await recoveryService.GetAllLostSatsAsync();
        if (allLostSats.IsEmpty())
            diagnostics.AppendLine("• Lost sats records: ✅ *None*");
        else
        {
            var totalLostSats = allLostSats.Sum(r => r.SatsAmount);
            diagnostics.AppendLine($"• User(s) with lost sats: *{allLostSats.Count}*");
            diagnostics.AppendLine($"• Total lost amount: ⚠️ *{totalLostSats.Format()}*");
            var oldestRecord = allLostSats.OrderBy(r => r.Timestamp).FirstOrDefault();
            if (oldestRecord is not null)
            {
                var age = (DateTimeOffset.Now - oldestRecord.Timestamp);
                diagnostics.AppendLine($"• Oldest record: *{age.TotalDays:N0} days ago*");
            }
        }

        // Lightning backend Information
        diagnostics.AppendLine("\n⚡ *Lightning backend status:*");
        diagnostics.AppendLine($"• Backend type: *{lightningBackend.BackendType}*");
        diagnostics.AppendLine($"• Sent requests: *{lightningBackend.SentRequests}*");

        // Exchange rate backend Information (optional)
        diagnostics.AppendLine("\n💱 *Exchange rate backend status:* ");
        if (exchangeRateBackend is null)
            diagnostics.AppendLine("• Backend: 🚫 *none*");
        else
        {
            diagnostics.AppendLine($"• Backend type: *{exchangeRateBackend.BackendType}*");
            diagnostics.AppendLineIfNotNull("• Last update: *{0}*", exchangeRateBackend.LastRateUpdate?.ToString("f"), "⚠️ never");
            if (exchangeRateBackend.RatesReliable)
                diagnostics.AppendLine($"• Fiat rate: *{exchangeRateBackend.FiatRate!.Value.FormatFiatRate()}*");
            diagnostics.AppendLine($"• Sent requests: *{exchangeRateBackend.SentRequests}*");
        }

        await botClient.SendMessage(command.ChatId,
            diagnostics.ToString(),
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Recovers lost sats from failed sessions.
    /// </summary>
    private async Task HandleRecoverCommandAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        // Check if command is used in private chat
        var chat = await botClient.GetChat(command.ChatId, cancellationToken);
        if (chat.Type != ChatType.Private)
            throw new InvalidOperationException("The recover command is only available in private chat with the bot.\n\n" +
                "Please send me `/recover` as a direct message.")
                .AddLogLevel(LogLevel.Warning);

        // Get lost sats for this user
        var lostSats = await recoveryService.TryGetLostSatsAsync(command.UserId);
        if (lostSats is null)
            throw new Exception("You *don't have any lost sats* to recover.\n\n" +
                "All your previous payments were successfully processed.")
                .AddLogLevel(LogLevel.Information);

        // Show recoverable amount
        var message = new StringBuilder()
            .AppendRecoveryMessage(lostSats)
            .ToString();
        await botClient.SendMessage(command.ChatId, message, 
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Processes a recovery invoice submitted by a user to recover their lost sats.
    /// </summary>
    /// <remarks>
    /// No need to check for sufficient balance here; if payment fails, recovery won't be cleared.
    /// </remarks>
    private async Task ProcessRecoveryInvoiceAsync(ITelegramBotClient botClient, User user, string bolt11, LostSatsRecord lostSats, CancellationToken cancellationToken)
    {
        // Calculate expected recovery amount
        var expectedSats = lostSats.SatsAmount;
        
        // Decode and validate the invoice
        var invoiceSats = lightningBackend.GetInvoiceAmount(bolt11);
        ValidateInvoiceAmount(expectedSats, invoiceSats);

        await botClient.SendMessage(user.Id, 
            "✅ Recovery invoice received!\n⏳ Processing recovery payment...", 
            cancellationToken: cancellationToken);

        // Attempt to pay the invoice
        var paymentResult = await lightningBackend.PayInvoiceAsync(bolt11, cancellationToken);
        if (paymentResult is not null)
        {
            await recoveryService.ClearLostSatsAsync(user.Id);
            
            await botClient.SendMessage(user.Id,
                $"🎉 *Recovery Completed!*\n\n" +
                $"✅ Successfully claimed *{expectedSats.Format()}*\n" +
                $"Your lost sats have been sent to your Lightning wallet! 🚀",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);

            logger.LogInformation("Successfully processed recovery payment for user {User}: {SatsAmount} sats from {Timestamp}", user, expectedSats.Format(), lostSats.Timestamp);
        }
        else
        {
            logger.LogError("Failed to process recovery payment for user {User}: {SatsAmount}", user, expectedSats.Format());
                
            throw new InvalidOperationException("Recovery payment failed!\n\n" +
                "Unable to process your recovery invoice. Please try again later or contact support.")
                .AddLogLevel(LogLevel.Error);
        }
    }


    #region Helper
    private void ValidateInvoiceAmount(long expectedSats, long invoiceSats)
    {
        if (invoiceSats != expectedSats)
            throw new InvalidOperationException($"Invoice amount mismatch!\n\n" +
                $"Expected: {expectedSats.Format()}\n" +
                $"Your invoice: {invoiceSats.Format()}\n" +
                "Please create a new invoice with the correct amount of sats.");
    }
    #endregion
}
