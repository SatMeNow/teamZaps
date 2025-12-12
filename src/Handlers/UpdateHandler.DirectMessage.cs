using System.Diagnostics;
using System.Text;
using teamZaps.Configuration;
using teamZaps.Services;
using teamZaps.Sessions;
using teamZaps.Utils;

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
                    "*Commands:*\n" +
                    "/startsession - Start a new payment session\n" +
                    "/closesession - Close payments and start lottery\n" +
                    "/cancelsession - Cancel session (admin only)\n" +
                    "/status - View session details\n\n" +
                    "*How it works:*\n" +
                    "1️⃣ Join the session using the button on the pinned message\n" +
                    "2️⃣ Send me payment amounts in *private chat*\n" +
                    "3️⃣ Pay the Lightning invoices I send you\n" +
                    "4️⃣ Join the lottery when payments close!\n\n" +
                    "💡 *All payments happen in private messages for privacy!*",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken);
                break;

            case "/diag":
                await HandleDiagnosisAsync(botClient, command, cancellationToken);
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
            return (true); // Ignore messages from users without active session

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
                var unit = grpCurrency.ToUnitName();
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
                    var invoice = await lnbitsService.CreateInvoiceAsync(invoiceAmount, unit, memo, cancellationToken).ConfigureAwait(false);
                    // Store as pending payment
                    var pending = new PendingPayment
                    {
                        User = user,
                        PaymentHash = invoice!.PaymentHash,
                        PaymentRequest = invoice.PaymentRequest,
                        Tokens = tokenGrp.ToArray(),
                        FiatAmount = grpAmount,
                        TipAmount = tipAmount,
                        Currency = grpCurrency,
                        CreatedAt = DateTimeOffset.UtcNow
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

                    logger.LogInformation("Payout executed successfully by {User} for session {Session}", winnerUser, session);
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
            logger.LogError(ex, "Error executing payout by {User} for session {Session}", winnerUser, session);
            await botClient.SendMessage(session.ChatId,
                "❌ Error during payout. Please contact support.",
                cancellationToken: cancellationToken);
        }
        
        await botClient.SendMessage(winnerUser.UserId,
            "✅ Payout completed.",
            cancellationToken: cancellationToken);
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
        {
            await botClient.SendMessage(command.ChatId,
                "❌ This command is only available in private chat with the bot.",
                cancellationToken: cancellationToken);
            return;
        }

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
        diagnostics.AppendLine($"• Uptime: *{DateTimeOffset.UtcNow - Process.GetCurrentProcess().StartTime:dd\\.hh\\:mm\\:ss}*");
        
        // Bot Information
        diagnostics.AppendLine("\n🤖 *Bot status:*");
        diagnostics.AppendLine($"• Active sessions: *{sessionManager.ActiveSessions.Count()}*");

        if (botBehaviour.MaxBudget is null)
            diagnostics.AppendLine("• Server budget: *Unlimited*");
        else
        {
            var consumed = sessionManager.ConsumedServerBudget;
            var available = sessionManager.AvailableServerBudget ?? 0;
            diagnostics.AppendLine($"• Server budget: *{consumed.Format()}* / *{botBehaviour.MaxBudget!.Value.Format()}*");
            diagnostics.AppendLine($"• Available budget: *{available.Format()}*");
        }
        diagnostics.AppendLineIfNotNull("• Total locked amount: *{0}*", sessionManager.FormatAmount(), "💤 none");

        // Lightning backend Information
        diagnostics.AppendLine("\n⚡ *Lightning backend status:*");
        diagnostics.AppendLine($"• Node type: *{lnbitsService.ServiceType}*");
        diagnostics.AppendLine($"• Sent requests: *{lnbitsService.SentRequests}*");

        await botClient.SendMessage(command.ChatId,
            diagnostics.ToString(),
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }
}
