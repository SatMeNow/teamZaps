using System.Diagnostics;
using System.Text;
using TeamZaps.Backends;
using TeamZaps.Configuration;
using TeamZaps.Logging;
using TeamZaps.Services;
using TeamZaps.Session;
using TeamZaps.Utils;
using Telegram.Bot.Types.ReplyMarkups;

namespace TeamZaps.Handlers;

public partial class UpdateHandler
{
    private async Task<bool> HandleGroupCommandAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        switch (command.Value)
        {
            case BotGroupCommand.StartSession:
                await HandleStartSessionAsync(botClient, command, cancellationToken).ConfigureAwait(false);
                break;
            case BotGroupCommand.CloseSession:
                await HandleCloseSessionAsync(botClient, command.ChatId, command.From, cancellationToken).ConfigureAwait(false);
                break;
            case BotGroupCommand.CancelSession:
                await HandleCancelSessionAsync(botClient, command.ChatId, command.From, cancellationToken).ConfigureAwait(false);
                break;

            case BotGroupCommand.Status:
                await HandleStatusAsync(botClient, command.ChatId, cancellationToken).ConfigureAwait(false);
                break;
            case BotGroupCommand.Statistics:
                var chatId = command.ChatId;
                var session = workflowService.TryGetSessionByChat(chatId);
                var adminOptions = await GetAdminOptions(session, chatId).ConfigureAwait(false);

                // Check permissions
                if ((!adminOptions.AllowNonAdminStatistics) && (!await IsUserAdminAsync(botClient, chatId, command.From, cancellationToken).ConfigureAwait(false)))
                    throw new InvalidOperationException("Only group administrators can view statistics.")
                        .AddLogLevel(LogLevel.Warning)
                        .AnswerUser()
                        .ExpireMessage();
                
                await GroupStatisticsMessage.SendAsync(botClient, statisticService, command.ChatId, cancellationToken).ConfigureAwait(false);
                break;
            case BotGroupCommand.Config:
                await HandleConfigCommandAsync(botClient, command, cancellationToken).ConfigureAwait(false);
                break;

            default: return (false);
        }
        return (true);
    }

    private async Task HandleStartSessionAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        var chat = await botClient.GetChat(command.ChatId).ConfigureAwait(false);
        if (chat.Type != ChatType.Group && chat.Type != ChatType.Supergroup)
            throw new InvalidOperationException("Sessions can only be started in group chats!")
                .AnswerUser()
                .ExpireMessage();

        // Check bot's permissions in this chat
        var botRole = await botClient.GetBotRoleAsync(command.ChatId, cancellationToken).ConfigureAwait(false);
        if (botRole is not ChatMemberAdministrator adminRole)
            throw new InvalidOperationException("I need to be an *administrator* in this group to start a session!")
                .AddHelp("Please *promote me to admin* and try again.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();

        // Obtain admin options for this chat
        var adminOptions = await GetAdminOptions(null, command.ChatId).ConfigureAwait(false);

        // Check permissions - use saved options or default
        if ((!adminOptions.AllowNonAdminSessionStart) && (!await IsUserAdminAsync(botClient, command.ChatId, command.From, cancellationToken).ConfigureAwait(false)))
            throw new InvalidOperationException("Only group administrators can start a session.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();
        
        // Check server-wide liquidity limits
        var minBudget = botBehaviour.Budget.Default;
        if (!CheckServerBudgetLimit(minBudget))
        {
            await liquidityLogService.LogAsync(LogTag.RejectCreateSession, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Sorry, starting a new session would *exceed my total available liquidity* 🫣")
                .AddHelp("Please try again later when some budgets are available.")
                .AddLogLevel(LogLevel.Information)
                .AnswerUser()
                .ExpireMessage();
        }

        // Check Cashu wallet reserve — the mint charges a fee_reserve on each winner payout (NUT-05 melt)
        if (cashuBackend is not null)
        {
            var walletBalance = await cashuBackend.GetBalanceAsync(cancellationToken).ConfigureAwait(false);
            if (walletBalance < cashuBackend.MinimumReserve)
            {
                var notif = $"⚠️ A session start was rejected: Cashu wallet balance {walletBalance.Format()} is below the minimum reserve of {cashuBackend.MinimumReserve.Format()}. Please top up the wallet now!";
                foreach (var rootUser in telegramSettings.RootUsers)
                {
                    try
                    {
                        await botClient.SendMessage(rootUser, notif, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to notify root user {RootUser} about Cashu reserve shortage.", rootUser);
                    }
                }
                throw new InvalidOperationException("I'm sorry, my Cashu wallet is running low and *cannot cover winner payouts* right now.")
                    .AddHelp("Please try again in a few minutes or contact support.")
                    .AddLogLevel(LogLevel.Warning)
                    .AnswerUser()
                    .ExpireMessage();
            }
        }

        // Obtain block header at session start
        var currentBlock = await indexerBackend.GetCurrentBlockAsync(cancellationToken).ConfigureAwait(false);

        var session = await workflowService.TryStartSessionAsync(chat, command).ConfigureAwait(false);
        if (session is not null)
        {
            session.AdminOptions = adminOptions;
            session.StartedAtBlock = currentBlock;
            session.BotCanPinMessages = adminRole.CanPinMessages;
            
            await SessionStatusMessage.SendAsync(session, botClient, workflowService, cancellationToken).ConfigureAwait(false);
            
            #if RELEASE
            // Delete requesting message
            await botClient.DeleteMessageAsync(command.Source, cancellationToken).ConfigureAwait(false);
            #endif
        }
        else
            throw new InvalidOperationException("A session is already active in this group!")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();
    }
    private async Task HandleCloseSessionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        var session = workflowService.TryGetSessionByChat(chatId);
        if (session is null)
            throw new InvalidOperationException("No active session in this group.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();

        // Check permissions
        if ((!session.AdminOptions.AllowNonAdminSessionClose) && (!await IsUserAdminAsync(botClient, chatId, user, cancellationToken).ConfigureAwait(false)))
            throw new InvalidOperationException("Only group administrators can close a session.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();

        if (session.Phase > SessionPhase.AcceptingOrders)
            throw new InvalidOperationException("Session has already moved past the order phase.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();

        // Check for available liquidity
        var totalSats = exchangeRateBackend.ToSats(session.OrdersFiatAmount);
        if (!CheckLockedSatsLimit(totalSats))
            throw new InvalidOperationException("Sorry, there is *not enough liquidity available* right now to cover all invoices 🫣")
                .AddHelp("Please try to *close the session again* in a few minutes.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();

        if (session.HasOrders)
        {
            // Transition to payment phase - create one consolidated invoice per participant:
            session.Phase = SessionPhase.WaitingForPayments;

            foreach (var participant in session.Participants.Values.Where(p => p.HasOrders))
            {
                try
                {
                    if (participant.Options.PreferredPaymentMethod == PaymentMethod.Cashu && cashuBackend is not null)
                        await CreateParticipantCashuRequestAsync(botClient, session, participant, cancellationToken).ConfigureAwait(false);
                    else
                        await CreateParticipantInvoiceAsync(botClient, session, participant, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to create invoice for participant {User} in session {Session}.", participant, session);
                }
            }

            logger.LogInformation("Order phase closed for session {Session}, invoices sent to {Count} participant(s).", session, session.PendingPayments.Count);

            // Update session status messages:
            await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);

            // Update user status messages for all participants:
            await UpdateAllParticipantStatusesAsync(session, botClient, cancellationToken).ConfigureAwait(false);

            var payMessage = await botClient.SendMessage(chatId, $"⚡ *Invoices have been sent* to all participants.\n\nPlease check your private chat and *pay now*.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken).ConfigureAwait(false);
            botClient.DeleteMessageAfterAsync(payMessage, TimeSpan.FromMinutes(1), cancellationToken);
        }
        else
            await HandleCancelSessionAsync(botClient, chatId, user, cancellationToken).ConfigureAwait(false);
    }
    private async Task HandleCancelSessionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        var session = workflowService.TryGetSessionByChat(chatId);
        if (session is null)
            return;

        // Check permissions
        if ((!session.AdminOptions.AllowNonAdminSessionCancel) && (!await IsUserAdminAsync(botClient, chatId, user, cancellationToken).ConfigureAwait(false)))
            throw new InvalidOperationException("Only group administrators can cancel a session.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();

        if (workflowService.TryCloseSession(chatId, true))
        {
            // Cancel outstanding unpaid invoices:
            await CancelPendingInvoicesAsync(botClient, session!, cancellationToken).ConfigureAwait(false);

            await SessionStatusMessage.UpdateAsync(session!, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
            
            // Update user status messages for all participants
            await UpdateAllParticipantStatusesAsync(session!, botClient, cancellationToken).ConfigureAwait(false);
        
            await botClient.SendMessage(chatId, "❌ Session has been cancelled and removed.", cancellationToken: cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Session {Session} cancelled by user {User}.", session, user);
        }
        else
            throw new InvalidOperationException("No active session to close.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();   
    }
    private async Task HandleForceCloseSessionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        var session = workflowService.TryGetSessionByChat(chatId);
        if (session is null)
            throw new InvalidOperationException("No active session in this group.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();

        // Admin-only, unconditionally:
        if (!await IsUserAdminAsync(botClient, chatId, user, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Only group administrators can force close a session.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();

        if (session.Phase != SessionPhase.WaitingForPayments)
            throw new InvalidOperationException("Session is not in the payment phase.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();

        // Check if removing unpaid participants would drop the budget below already paid amount:
        var pendingPayments = session.PendingPayments.Values.ToArray();
        var removedBudget = session.PendingPayments.Values
            .Select(p => session.LotteryParticipants[p.Participant])
            .Sum();
        var remainingBudget = (session.Budget - removedBudget);
        if (remainingBudget < session.OrdersFiatAmount)
            throw new InvalidOperationException($"I'm sorry, but we *cannot force-close*!\n\n" + 
                $"Removing unpaid participants would *drop the session budget* below the already collected *{session.FiatAmount.Format()}* 🤷‍♂️")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();

        #if !DEBUG
        // Enforce minimum payment phase duration:
        if (!session.CanForceClose)
        {
            var remaining = session.RemainingForceCloseTime;
            throw new InvalidOperationException($"Don't hurry! *Let's wait* another {remaining:mm\\:ss} minute(s) to force close this session.")
                .AddLogLevel(LogLevel.Information)
                .AnswerUser()
                .ExpireMessage();
        }
        #endif

        logger.LogInformation("Force closing payment phase for session {Session}.", session);
            
        if (session.HasPayments)
        {
            // Remove unpaid participants from the session and cancel their invoices:
            foreach (var pending in pendingPayments)
            {
                var canceledInvoice = await CancelPendingInvoiceAsync(botClient, session, pending.PaymentHash, cancellationToken).ConfigureAwait(false);

                workflowService.RemoveParticipant(session, pending.UserId);

                // Notify user:
                var removeText = $"⚠️ You have been *removed* from the session in *{session.ChatTitle}*!\n\n";
                if (!canceledInvoice)
                    removeText += "☝️ So *don't pay your lightning invoice!*\n";
                removeText += $"Instead, you will have to *pay your bill* of {pending.FiatAmount.Format()} by your own *in cash*.";
                await botClient.SendMessage(pending.UserId, removeText, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            Debug.Assert(session.PendingPayments.IsEmpty);

            // Notify group:
            var removedUsers = string.Join("\n", pendingPayments
                .Select(p => $"• {p.Participant.MarkdownDisplayName()}"));
            await botClient.SendMessage(chatId, $"ℹ️ Session has been *force-closed*.\n\n" +
                $"⚠️ The following *participant(s)* did not pay in time and *have been removed*:\n{removedUsers}\n\n" +
                $"They will need to pay their own bills *in cash*.",
                parseMode: ParseMode.Markdown, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (session.HasPayments)
            await paymentMonitorService.DrawLotteryAsync(session, cancellationToken).ConfigureAwait(false);
        else
            // Regular cancel session if there are no payments:
            await HandleCancelSessionAsync(botClient, chatId, user, cancellationToken).ConfigureAwait(false);
    }
    private async Task HandleJoinSessionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        var session = workflowService.TryGetSessionByChat(chatId);
        // Check if session exists
        if (session is null)
            throw new InvalidOperationException("No active session in this group.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();
        // Check if user is already a participant in this session
        if (session.Participants.ContainsKey(user.Id))
            throw new InvalidOperationException("You're already part of this session.")
                .AddLogLevel(LogLevel.Information)
                .AnswerUser()
                .ExpireMessage();
        // Check if session is accepting new participants
        if (session.Phase > SessionPhase.AcceptingOrders)
            throw new InvalidOperationException("Session is currently not accepting new participants.")
                .AddLogLevel(LogLevel.Information)
                .AnswerUser()
                .ExpireMessage();
        // Check if user is already participating in another session
        var existingSession = workflowService.TryGetSessionByUser(user.Id);
        if ((existingSession is not null) && (existingSession.ChatId != chatId))
        {
            var existingChat = await botClient.GetChat(existingSession.ChatId, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"You're already participating in a session in *{existingChat.Title}*!\n\n" +
                "You can only join one session at a time. Please complete your current session first.")
                .AddLogLevel(LogLevel.Information)
                .AnswerUser()
                .ExpireMessage();
        }

        // Check for lost sats of this user
        var lostSats = await recoveryService.TryGetLostSatsAsync(user.Id).ConfigureAwait(false);
        if (lostSats is not null)
        {
            var message = new StringBuilder()
                .AppendRecoveryMessage(lostSats)
                .AppendLine("\n⚠️ You need to *recover your lost sats* before joining a new session.")
                .ToString();
            throw new Exception(message)
                .AddLogLevel(LogLevel.None)
                .AnswerUser(user.Id);
        }

        // Send private status message
        try
        {
            var participant = await workflowService.EnsureParticipantAsync(session, user).ConfigureAwait(false);
            await UserStatusMessage.SendAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!sessionManager.PendingJoins.ContainsKey(user.Id))
            {
                // Mark user as pending session join
                sessionManager.PendingJoins[user.Id] = new PendingJoinInfo(chatId);
                // Remove user from participants to avoid inconsistencies
                session.Participants.Remove(user.Id, out _);
            }

            // Update the single shared welcome message for this chat
            if (session.PendingWelcome is not null)
            {
                session.PendingWelcome!.PendingUsers.Add(user);
                await WelcomeMessage.UpdateAsync(session, botClient, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var botUser = await botClient.GetMe(cancellationToken).ConfigureAwait(false);
                await WelcomeMessage.SendAsync(session, user, botClient, cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation("Invited new user {User} to a private bot chat.", user.DisplayName());
            return;
        }

        // Update the pinned status message
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("User {User} joined session in chat {ChatId}.", user, chatId);
    }

    /// <summary>
    /// Views the current session status.
    /// </summary>
    /// <remarks>
    /// Maybe useful if the status message is far above in chat history and you want to move it down.
    /// </remarks>
    private async Task HandleStatusAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        var session = workflowService.TryGetSessionByChat(chatId);
        if (session is null)
            throw new InvalidOperationException($"No active session in this group.\n\nUse {BotGroupCommand.StartSession} to start one!")
                .AnswerUser()
                .ExpireMessage();

        // Delete previous message (should exist, but we don't really know):
        if (session.StatusMessageId is not null)
            await botClient.DeleteMessage(chatId, session.StatusMessageId!.Value).ConfigureAwait(false);

        await SessionStatusMessage.SendAsync(session, botClient, workflowService, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleConfigCommandAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        var chatId = command.ChatId;
        var chatTitle = command.Source.Chat.Title!;
        var user = command.From;

        // Check if user is admin
        if (!await IsUserAdminAsync(botClient, chatId, user, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Only group administrators can change settings.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();

        var session = workflowService.TryGetSessionByChat(chatId);
        var options = await GetAdminOptions(session, chatId).ConfigureAwait(false);

        // Respond to user's private bot chat
        var message = await ConfigMessage.SendAsync(botClient, user.Id, chatTitle, options, cancellationToken).ConfigureAwait(false);
        
        // Store mapping from config message ID to group chat ID
        configMessageMap[message.MessageId] = chatId;

        // [Bug?] Fails to delete (Telegram responds `Bad request: message can't be deleted`)
        // await DeleteMessageAsync(botClient, chatId, command.Source.MessageId, cancellationToken).ConfigureAwait(false);
    }
    private async Task HandleSetOptionsAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        if (!configMessageMap.TryGetValue(query.Message!.MessageId, out var groupChatId))
            throw new InvalidOperationException($"Sorry, can't associate config message with group. Please run {BotGroupCommand.Config} again.")
                .AnswerUser()
                .ExpireMessage();
            
        var chatId = query.Message!.Chat.Id;
        var chatTitle = query.Message!.Chat.Title!;

        var session = workflowService.TryGetSessionByChat(groupChatId);
        var options = await GetAdminOptions(session, groupChatId).ConfigureAwait(false);

        // Toggle the selected option
        var optionName = query.Data!.Split('_').Last();
        switch (optionName)
        {
            case "start": options.AllowNonAdminSessionStart = !options.AllowNonAdminSessionStart; break;
            case "close": options.AllowNonAdminSessionClose = !options.AllowNonAdminSessionClose; break;
            case "statistics": options.AllowNonAdminStatistics = !options.AllowNonAdminStatistics; break;
            case "cancel": options.AllowNonAdminSessionCancel = !options.AllowNonAdminSessionCancel; break;

            default: return;
        }

        // Save options
        await adminOptionsService.WriteAsync(groupChatId, options).ConfigureAwait(false);

        // Update session if active
        if (session is not null)
            session.AdminOptions = options;

        await ConfigMessage.UpdateAsync(botClient, chatId, query.Message.MessageId, chatTitle, options, cancellationToken).ConfigureAwait(false);

        await botClient.AnswerCallbackQuery(query.Id, "✅ Setting updated", cancellationToken: cancellationToken).ConfigureAwait(false);
    }


    #region Helper
    private async Task CancelPendingInvoicesAsync(ITelegramBotClient botClient, SessionState session, CancellationToken cancellationToken)
    {
        foreach (var paymentHash in session.PendingPayments.Keys)
            await CancelPendingInvoiceAsync(botClient, session, paymentHash, cancellationToken).ConfigureAwait(false);

        Debug.Assert(session.PendingPayments.IsEmpty);
    }
    private async Task<bool> CancelPendingInvoiceAsync(ITelegramBotClient botClient, SessionState session, string paymentHash, CancellationToken cancellationToken)
    {
        var result = false;
        try
        {
            if (session.PendingPayments.TryRemove(paymentHash, out var removed))
            {
                if (lightningBackend is ISupportsCancelInvoice cancelBackend)
                {
                    result = await cancelBackend.CancelInvoiceAsync(paymentHash, cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("Cancelled pending invoice {PaymentHash} for session {Session}.", paymentHash, session);
                }

                if (removed.MessageId is not null)
                {
                    var status = (result ? PaymentStatus.Canceled : PaymentStatus.Removed);
                    await LightningPaymentMessage.UpdateAsync(removed, status, botClient, logger, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cancel pending invoice {PaymentHash} for session {Session}.", paymentHash, session);
        }
        return (result);
    }
    private int SelectWinners(SessionState session) => LotteryHelper.SelectWinners(session);

    private bool CheckLockedSatsLimit(long requestedBudget) => (requestedBudget <= (sessionManager.AvailableLockedSats ?? long.MaxValue));
    private bool CheckServerBudgetLimit(double requestedBudget) => (requestedBudget <= (sessionManager.AvailableServerBudget ?? double.MaxValue));

    private async Task CreateParticipantInvoiceAsync(ITelegramBotClient botClient, SessionState session, ParticipantState participant, CancellationToken cancellationToken)
    {
        var user = participant.User;
        var totalFiat = participant.OrdersFiatAmount;
        var totalTip = participant.OrdersTipAmount;
        var totalSats = exchangeRateBackend.ToSats(totalFiat);
        var allTokens = participant.Orders.SelectMany(o => o.Tokens).ToArray();
        var memo = $"{session.ChatTitle}: invoice for {user}";

        var invoice = await lightningBackend.CreateInvoiceAsync(totalSats, memo, cancellationToken).ConfigureAwait(false);

        // Store as pending payment:
        var pending = new PendingPayment
        {
            Participant = participant,
            PaymentHash = invoice.PaymentHash,
            PaymentRequest = invoice.PaymentRequest,
            Tokens = allTokens,
            SatsAmount = totalSats,
            TipAmount = totalTip,
            FiatAmount = totalFiat,
            Currency = BotBehaviorOptions.AcceptedFiatCurrency,
            CreatedAt = DateTimeOffset.Now
        };
        session.PendingPayments.TryAdd(invoice.PaymentHash, pending);

        // Send invoice to participant:
        var message = await LightningPaymentMessage.SendAsync(pending, botClient, cancellationToken).ConfigureAwait(false);
        pending.MessageId = message.MessageId;

        logger.LogInformation("Created payment invoice for participant {User} in session {Session}: {Amount}.", user, session, totalFiat.Format());
    }

    private async Task CreateParticipantCashuRequestAsync(ITelegramBotClient botClient, SessionState session, ParticipantState participant, CancellationToken cancellationToken)
    {
        var user = participant.User;
        var totalFiat = participant.OrdersFiatAmount;
        var totalTip = participant.OrdersTipAmount;
        var totalSats = exchangeRateBackend.ToSats(totalFiat);
        var allTokens = participant.Orders.SelectMany(o => o.Tokens).ToArray();

        // Push-based: no invoice is created; the user will paste a cashuA token to the bot.
        var pending = new PendingPayment
        {
            Participant = participant,
            PaymentHash = Guid.NewGuid().ToString("N"), // local identifier, no mint contact
            PaymentRequest = null,                       // marks this as a Cashu token payment
            Tokens = allTokens,
            SatsAmount = totalSats,
            TipAmount = totalTip,
            FiatAmount = totalFiat,
            Currency = BotBehaviorOptions.AcceptedFiatCurrency,
            CreatedAt = DateTimeOffset.Now
        };
        session.PendingPayments.TryAdd(pending.PaymentHash, pending);

        // Prompt participant to paste a cashuA token:
        var message = await CashuPaymentMessage.SendAsync(pending, botClient, cancellationToken).ConfigureAwait(false);
        pending.MessageId = message.MessageId;

        logger.LogInformation("Created Cashu payment request for participant {User} in session {Session}: {Amount}.", user, session, totalFiat.Format());
    }

    private async Task<BotAdminOptions> GetAdminOptions(SessionState? session, long chatId)
    {
        BotAdminOptions? options = null;
        if (options is null)
            // Take options from active session:
            options = session?.AdminOptions;
        if (options is null)
            // Load saved options:
            options = await adminOptionsService.ReadAsync(chatId).ConfigureAwait(false);
        if (options is null)
            // Create new options:
            options = new BotAdminOptions();

        return (options);
    }
    #endregion

    
    private readonly Dictionary<int, long> configMessageMap = new(); // Config message ID -> group chat ID
}
