using System.Diagnostics;
using System.Text;
using TeamZaps;
using TeamZaps.Backend;
using TeamZaps.Configuration;
using TeamZaps.Helper;
using TeamZaps.Services;
using TeamZaps.Session;
using TeamZaps.Statistic;
using TeamZaps.Utils;
using Telegram.Bot.Types.ReplyMarkups;

namespace TeamZaps.Handlers;

public partial class UpdateHandler
{
    private async Task<bool> HandleDirectCommandAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        switch (command.Value)
        {
            case BotPmCommand.Start:
                // Get pending join
                sessionManager.PendingJoins.TryRemove(command.UserId, out var pendingJoin);

                // Send welcome message
                var welcomeMessage = new StringBuilder()
                    .AppendLine("Welcome to Team Zaps! 🎯\n")
                    .AppendLine("I help groups split bills using Bitcoin Lightning!\n")
                    .AppendLine("*How it works:*")
                    .AppendLine("1️⃣ Someone starts a session in your group")
                    .AppendLine("2️⃣ Join the session using the _Join_ button")
                    .AppendLine("3️⃣ Send me payments as direct message")
                    .AppendLine("4️⃣ One random participant wins the pot!\n")
                    .Append($"Use `{BotPmCommand.Help}` for detailed instructions.");
                if (pendingJoin is not null)
                    welcomeMessage.AppendLine("\n\n💡 Okay, let's continue and join the session 🏃‍➡️");
                await botClient.SendMessage(command.ChatId, 
                    welcomeMessage.ToString(),
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                // Send private status message if pending
                if (pendingJoin is not null)
                {
                    await HandleJoinSessionAsync(botClient, pendingJoin.ChatId, command.Source.From!, cancellationToken).ConfigureAwait(false);
                    await DeleteMessageAsync(botClient, pendingJoin.ChatId, pendingJoin.WelcomeMessageId, cancellationToken).ConfigureAwait(false);
                }
                break;

            case BotPmCommand.Recover:
                await HandleRecoverCommandAsync(botClient, command, cancellationToken).ConfigureAwait(false);
                break;

            case BotPmCommand.Statistics:
                if (await IsRootUserAsync(botClient, command, cancellationToken).ConfigureAwait(false))
                    await ServerStatisticsMessage.SendAsync(botClient, statisticService, command.ChatId, cancellationToken).ConfigureAwait(false);
                await UserStatisticsMessage.SendAsync(botClient, statisticService, command.From, cancellationToken).ConfigureAwait(false);
                break;

            case BotPmCommand.Help:
                await botClient.SendMessage(command.ChatId,
                    "🎯 *Team Zaps help*\n\n" +
                    "*Group commands* (use in a group chat):\n" +
                    $"{BotGroupCommand.StartSession} - Start a new payment session (maybe for admins only)\n" +
                    $"{BotGroupCommand.CloseSession} - Close payments and start lottery (maybe for admins only)\n" +
                    $"{BotGroupCommand.CancelSession} - Cancel session (maybe for admins only)\n\n" +
                    $"{BotGroupCommand.Statistics} - Show group statistics (may be restricted to admins)\n\n" +
                    $"{BotGroupCommand.Config} - Configure group settings and bot behavior (admins only)\n\n" +
                    "*Private commands* (use in direct message with the bot):\n" +
                    $"{BotPmCommand.Statistics} - Show personal and server statistics\n" +
                    $"{BotPmCommand.Recover} - Recover lost sats from interrupted sessions\n" +
                    $"{BotPmCommand.Help} - Show this help message\n" +
                    $"{BotPmCommand.About} - About this bot\n\n" +
                    "*How to participate:*\n" +
                    "1️⃣ Join the session using the button on the status message in the group\n" +
                    "2️⃣ Send payment amounts here in *private chat*\n" +
                    "3️⃣ Pay the Lightning invoices I send you\n" +
                    "4️⃣ If you opted into the lottery, wait for the draw when the admin closes payments\n\n" +
                    "💡 *Payments and invoices are handled in private messages for privacy.*\n\n" +
                    "ℹ️ For *detailed info*, check out the [GitHub Repository](https://github.com/SatMeNow/teamZaps).",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            case BotPmCommand.About:
                var aboutMessage = new StringBuilder()
                    .AppendLine("ℹ️ *Team Zaps* 🎯⚡")
                    .AppendLine()
                    .AppendLine("*Split bills with Bitcoin Lightning in Telegram groups!*\n")
                    .AppendLine("Team Zaps helps groups coordinate Lightning payments for shared expenses. " +
                        "When Bitcoin isn't accepted at your favorite restaurant or bar, use Team Zaps to let everyone pay in sats while one person handles the fiat transaction.")
                    .AppendLine()
                    .AppendLine("🚀 *Application:*")
                    .AppendLine($"• Version: *v{UtilAssembly.GetVersion()}*")
                    .AppendLine($"• .NET: *{Environment.Version}*")
                    .AppendLine()
                    .AppendLine("🧑‍💻 *Open Source:*")
                    .AppendLine("• [GitHub repository](https://github.com/SatMeNow/teamZaps)")
                    .AppendLine()
                    .Append($"Use `{BotPmCommand.Help}` for commands and instructions.");
                
                await botClient.SendMessage(command.ChatId,
                    aboutMessage.ToString(),
                    parseMode: ParseMode.Markdown,
                    linkPreviewOptions: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            case BotRootCommand.Diagnosis:
                if (await IsRootUserAsync(botClient, command, cancellationToken).ConfigureAwait(false))
                    await HandleDiagnosisAsync(botClient, command, cancellationToken).ConfigureAwait(false);
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
                var lostSats = await recoveryService.TryGetLostSatsAsync(user.Id).ConfigureAwait(false);
                if (lostSats is not null)
                {
                    await ProcessRecoveryInvoiceAsync(botClient, user, recoveryInvoice, lostSats, cancellationToken).ConfigureAwait(false);
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
                        await ProcessPrivatePaymentAsync(botClient, session, user, tokens, text, cancellationToken).ConfigureAwait(false);
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
                            await ProcessWinnerInvoiceAsync(botClient, session, winnerUser, invoice, cancellationToken).ConfigureAwait(false);
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
                    var invoice = await lightningBackend.CreateInvoiceAsync(invoiceAmount, grpCurrency, memo, cancellationToken).ConfigureAwait(false);
                    if (invoice is null)
                        throw new InvalidOperationException("Failed with internal error when creating invoice!");

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
            await botClient.SendException(user, ex, cancellationToken).ConfigureAwait(false);
        }
    }
    private async Task ProcessWinnerInvoiceAsync(ITelegramBotClient botClient, SessionState session, ParticipantState winnerUser, string bolt11, CancellationToken cancellationToken)
    {
        logger.LogInformation("Winner invoice submitted by {User} for session {Session}", winnerUser, session);

        var winnerInfo = session.Winners[winnerUser];

        // Obtain block header at session end
        var currentBlock = await indexerBackend.GetCurrentBlockAsync(cancellationToken).ConfigureAwait(false);

        // Decode and validate the invoice amount
        var invoiceSats = lightningBackend.GetInvoiceAmount(bolt11);
        ValidateInvoiceAmount(winnerInfo.SatsAmount, invoiceSats);

        winnerUser!.SubmittedInvoice = true;

        await botClient.SendMessage(winnerUser.UserId, "✅ Invoice received!\n⏳ Processing payout...", cancellationToken: cancellationToken).ConfigureAwait(false);

        try
        {
            var paymentResult = await lightningBackend.PayInvoiceAsync(bolt11!, cancellationToken).ConfigureAwait(false);
            if (paymentResult is null)
                throw new InvalidOperationException("Failed to execute payout. Please try again later.");
                
            if (session.PayoutCompleted)
            {
                session.Phase = SessionPhase.Completed;

                await WinnerMessage.UpdateAsync(session, PaymentStatus.Paid, paymentResult, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
            }
            
            session.CompletedAtBlock = currentBlock;

            // Update the pinned status message
            await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
            
            // Update user status messages for all participants
            foreach (var participant in session.Participants.Values)
                await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
            
            if (session.Phase.IsClosed())
            {
                // Clean up session
                workflowService.TryCloseSession(session, false);

                logger.LogInformation("Payout executed successfully by {User} for session {Session}", winnerUser, session);
            }
        
            await botClient.SendMessage(winnerUser.UserId,
                $"🎉 *Session completed!*\n\n" +
                $"✅ Successfully paid out *{invoiceSats.Format()}*.",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await statisticService.OnSessionCompleteAsync(session).ConfigureAwait(false);
            Debug.Assert(session.Statistics is not null);

            // Send statistics
            await GroupStatisticsMessage.SendIfAsync(botClient, statisticService, session, cancellationToken).ConfigureAwait(false);
            foreach (var participant in session.Participants.Values)
                await UserStatisticsMessage.SendIfAsync(botClient, statisticService, participant, cancellationToken).ConfigureAwait(false);
            
            await liquidityLogService.LogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing payout by {User} for session {Session}", winnerUser, session);
            await botClient.SendMessage(session.ChatId, "❌ Error during payout. Please contact support.", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
    /// <summary>
    /// Shows diagnostic information about the current session and system state.
    /// </summary>
    /// <remarks>
    /// Useful for debugging and monitoring session health.
    /// </remarks>
    private async Task HandleDiagnosisAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        var diag = new StringBuilder();
        diag.AppendLine("🔍 *DIAGNOSTIC INFORMATION*");
        
        // Environment Information
        diag.AppendLine("\n🖥️ *Environment:*");
        diag.AppendLine($"• OS: *{Environment.OSVersion}*");
        diag.AppendLine($"• .NET: *{Environment.Version}*");
        diag.AppendLine($"• Host configuration: *{hostEnvironment.EnvironmentName}*");
        #if !DEBUG
        diag.AppendLine($"• Timezone: *{TimeZoneInfo.Local.DisplayName}*");
        diag.AppendLine($"• Machine: *{Environment.MachineName}*");
        diag.AppendLine($"• User: *{Environment.UserName}*");
        #endif

        // Application Information
        var asmName = UtilAssembly.GetInfo();
        diag.AppendLine("\n🚀 *Application:*");
        diag.AppendLine($"• Version: *v{UtilAssembly.GetVersion()}*");
        diag.AppendLine($"• Process ID: *{Environment.ProcessId}*");
        diag.AppendLine($"• Is 64bit process: *{Environment.Is64BitProcess}*");
        diag.AppendLine($"• Is privileged process: *{Environment.IsPrivilegedProcess}*");

        // System Information
        diag.AppendLine("\n⚙️ *System status:*");
        diag.AppendLine($"• CPU usage: *{Environment.CpuUsage.TotalTime}*");
        diag.AppendLine($"• Memory usage: *{GC.GetTotalMemory(false) / 1024 / 1024:N0} MB*");
        diag.AppendLine($"• Uptime: *{DateTimeOffset.Now - Process.GetCurrentProcess().StartTime:dd\\.hh\\:mm\\:ss}*");
        
        // Bot Information
        diag.AppendLine("\n🤖 *Bot status:*");
        var maxSessions = "";
        if (botBehaviour.MaxParallelSessions > 0)
            maxSessions = $" of *{botBehaviour.MaxParallelSessions.Value}*";
        diag.AppendLine($"• Active sessions: *{sessionManager.ActiveSessions.Count()}*{maxSessions}");

        if (botBehaviour.MaxBudget is null)
            diag.AppendLine("• Server budget: *Unlimited*");
        else
        {
            var consumed = sessionManager.ConsumedServerBudget;
            if (consumed > 0)
                diag.AppendLine($"• Server budget: *{consumed.Format()}* / *{botBehaviour.MaxBudget!.Value.Format()}*");
            var available = sessionManager.AvailableServerBudget ?? 0;
            if (available > 0)
                diag.AppendLine($"• Available budget: *{available.Format()}*");
        }
        diag.AppendLineIfNotNull("• Total locked amount: *{0}*", sessionManager.FormatAmount(), "💤 none");

        // Lost and Found Recovery Information
        diag.AppendLine("\n🔍 *Lost and Found recovery:*");
        var allLostSats = await recoveryService.GetAllLostSatsAsync().ConfigureAwait(false);
        if (allLostSats.IsEmpty())
            diag.AppendLine("• Lost sats records: ✅ *None*");
        else
        {
            var totalLostSats = allLostSats.Sum(r => r.SatsAmount);
            diag.AppendLine($"• User(s) with lost sats: *{allLostSats.Count}*");
            diag.AppendLine($"• Total lost amount: ⚠️ *{totalLostSats.Format()}*");
            var oldestRecord = allLostSats.OrderBy(r => r.Timestamp).FirstOrDefault();
            if (oldestRecord is not null)
            {
                var age = (DateTimeOffset.Now - oldestRecord.Timestamp);
                diag.AppendLine($"• Oldest record: *{age.TotalDays:N0} days ago*");
            }
        }

        Action<IBackend> appendBackendInfo = (backend) =>
        {
            diag.AppendLine($"• Service: *{backend.BackendType}*");
            if (backend is IMultiConnectionBackend multiConnection)
            {
                diag.AppendLine($"• Configured hosts: *{multiConnection.Hosts.Count()}*");
                foreach (var host in multiConnection.Hosts)
                {
                    string? succeeded = (host.SentRequests > 0) ? $"*{host.SentRequests}*x✅ " : null;
                    string? failed = (host.FailedRequests > 0) ? $"*{host.FailedRequests}*x⛔ " : null;
                    diag.AppendLine($"   • {succeeded}{failed}{host.Hostname}");
                }
            }
            else
            {
                string? succeeded = (backend.SentRequests > 0) ? $"✅ *{backend.SentRequests}*" : "*0*";
                diag.AppendLine($"• Sent requests: {succeeded}");
                if (backend.FailedRequests > 0)
                    diag.AppendLine($"• Failed requests: ⛔ *{backend.FailedRequests}*"); 
            }
        };

        // Indexer backend Information
        diag.AppendLine("\n🗂️ *Indexer backend status:*");
        appendBackendInfo(indexerBackend);
        diag.AppendLineIfNotNull("• Last block: {0}", indexerBackend.LastBlock?.Format());

        // Lightning backend Information
        diag.AppendLine("\n⚡ *Lightning backend status:*");
        appendBackendInfo(lightningBackend);

        // Exchange rate backend Information (optional)
        diag.AppendLine("\n💱 *Exchange rate backend status:* ");
        if (exchangeRateBackend is null)
            diag.AppendLine("• Backend: 🚫 *none*");
        else
        {
            appendBackendInfo(exchangeRateBackend);
            diag.AppendLineIfNotNull("• Last update: *{0}*", exchangeRateBackend.LastRateUpdate?.ToString("f"), "⚠️ never");
            if (exchangeRateBackend.RatesReliable)
                diag.AppendLine($"• Fiat rate: *{exchangeRateBackend.FiatRate!.Value.FormatFiatRate()}*");
        }

        await botClient.SendMessage(command.ChatId,
            diag.ToString(),
            parseMode: ParseMode.Markdown,
            linkPreviewOptions: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recovers lost sats from failed sessions.
    /// </summary>
    private async Task HandleRecoverCommandAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        // Check if command is used in private chat
        var chat = await botClient.GetChat(command.ChatId, cancellationToken).ConfigureAwait(false);
        if (chat.Type != ChatType.Private)
            throw new InvalidOperationException("The recover command is only available in private chat with the bot.\n\n" +
                "Please send me `/recover` as a direct message.")
                .AddLogLevel(LogLevel.Warning);

        // Get lost sats for this user
        var lostSats = await recoveryService.TryGetLostSatsAsync(command.UserId).ConfigureAwait(false);
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
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Attempt to pay the invoice
        var paymentResult = await lightningBackend.PayInvoiceAsync(bolt11, cancellationToken).ConfigureAwait(false);
        if (paymentResult is not null)
        {
            await recoveryService.ClearLostSatsAsync(user.Id).ConfigureAwait(false);
            
            await botClient.SendMessage(user.Id,
                $"🎉 *Recovery completed!*\n\n" +
                $"✅ Successfully claimed *{expectedSats.Format()}*\n" +
                $"Your lost sats have been sent to your Lightning wallet! 🚀",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken).ConfigureAwait(false);

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
    private async Task<bool> IsRootUserAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        // Check if user is a root user
        if (!telegramSettings.RootUsers.Contains(command.UserId))
            return (false);

        // Check if command is used in private chat
        var chat = await botClient.GetChat(command.ChatId, cancellationToken).ConfigureAwait(false);
        if (chat.Type != ChatType.Private)
            throw new InvalidOperationException("This command is only available in private chat with the bot.");

        return (true);
    }
    #endregion
}

