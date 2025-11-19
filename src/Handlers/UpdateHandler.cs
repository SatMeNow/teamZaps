using System.Diagnostics.CodeAnalysis;
using System.Text;
using teamZaps.Services;
using teamZaps.Sessions;
using teamZaps.Utils;

namespace teamZaps.Handlers;

public class UpdateHandler : IUpdateHandler
{
    private readonly ILogger<UpdateHandler> _logger;
    private readonly LnbitsService _lnbitsService;
    private readonly SessionManager _sessionManager;
    private readonly SessionWorkflowService _workflowService;

    public UpdateHandler(
        ILogger<UpdateHandler> logger, 
        LnbitsService lnbitsService,
        SessionManager sessionManager,
        SessionWorkflowService workflowService)
    {
        _logger = logger;
        _lnbitsService = lnbitsService;
        _sessionManager = sessionManager;
        _workflowService = workflowService;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            switch (update.Type)
            {
                case UpdateType.Message:
                    await HandleMessageAsync(botClient, update.Message!, cancellationToken);
                    break;
                case UpdateType.CallbackQuery:
                    await HandleCallbackQueryAsync(botClient, update.CallbackQuery!, cancellationToken);
                    break;
                case UpdateType.EditedMessage:
                    await HandleEditedMessageAsync(botClient, update.EditedMessage!, cancellationToken);
                    break;
                case UpdateType.ChannelPost:
                case UpdateType.EditedChannelPost:
                    // Handle channel posts if needed
                    break;
                default:
                    _logger.LogInformation("Received update type: {UpdateType}", update.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling update");
        }
    }

    public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
    {
        string errorMessage;
        if (exception is ApiRequestException apiRequestException)
        {
            errorMessage = $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}";
        }
        else
        {
            errorMessage = exception.ToString();
        }

        _logger.LogError("HandleError: {ErrorMessage}", errorMessage);
        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        _logger.LogInformation("Received message from {ChatId}: {MessageText}", chatId, message.Text);

        // Only process group messages for session functionality
        if (message.Chat.Type != ChatType.Private && message.Chat.Type != ChatType.Channel)
        {
            if (message.IsCommand())
            {
                await HandleCommandAsync(botClient, message, cancellationToken);
            }
        }
        else if (message.IsCommand())
        {
            await HandleCommandAsync(botClient, message, cancellationToken);
        }
        else
        {
            // Check if this is an invoice submission in DM
            await HandleDirectMessageAsync(botClient, message, cancellationToken);
        }
    }

    private async Task HandleCommandAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var userId = message.From!.Id;
        message.GetCommand(out var cmd, out var args);
        
        try
        {
            switch (cmd)
            {
                case "/start":
                    await botClient.SendMessage(chatId, 
                        "Welcome to Team Zaps! 🎯\n\n" +
                        "I help groups split bills using Bitcoin Lightning!\n\n" +
                        "*How it works:*\n" +
                        "1️⃣ Someone starts a session in your group\n" +
                        "2️⃣ Join the session using the button\n" +
                        "3️⃣ Send me payment amounts privately\n" +
                        "4️⃣ One random participant wins the pot!\n\n" +
                        "Use /help for detailed instructions.", 
                        parseMode: ParseMode.Markdown,
                        cancellationToken: cancellationToken);
                    break;

                case "/help":
                    await botClient.SendMessage(chatId,
                        "🎯 *Team Zaps Help*\n\n" +
                        "*Commands:*\n" +
                        "/startsession - Start a new payment session\n" +
                        "/closesession - Close payments and start lottery\n" +
                        "/cancelsession - Cancel session (admin only)\n" +
                        "/status - View session details\n\n" +
                        "*How it works:*\n" +
                        "1️⃣ Join the session using the button on the pinned message\n" +
                        "2️⃣ Send me payment amounts in *private chat*:\n" +
                        "   • `3,99` (€ per default)\n" +
                        "   • `5,50eur` or `5€`\n" +
                        "   • `2eur+1000sat`\n" +
                        "3️⃣ Pay the Lightning invoices I send you\n" +
                        "4️⃣ Join the lottery when payments close!\n\n" +
                        "💡 *All payments happen in private messages for privacy!*",
                        parseMode: ParseMode.Markdown,
                        cancellationToken: cancellationToken);
                    break;

                case "/startsession":
                    await HandleStartSessionAsync(botClient, message, cancellationToken);
                    break;

                case "/closesession":
                    await HandleCloseSessionAsync(botClient, chatId, userId, cancellationToken);
                    break;

                case "/cancelsession":
                    await HandleCancelSessionAsync(botClient, chatId, userId, cancellationToken);
                    break;

                case "/status":
                    await HandleStatusAsync(botClient, message, cancellationToken);
                    break;

#if DEBUG
                case "/balance":
                    var walletDetails = await _lnbitsService.GetWalletDetailsAsync(cancellationToken);
                    var balanceMsg = walletDetails is null 
                        ? "Failed to fetch wallet details." 
                        : $"Wallet balance: {walletDetails.Balance} sats";
                    await botClient.SendMessage(chatId, balanceMsg, cancellationToken: cancellationToken);
                    break;
#endif

                default:
                    await botClient.SendMessage(chatId, 
                        "Unknown command. Use /help to see available commands.", 
                        cancellationToken: cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling command: {cmd}", cmd);
            await botClient.SendMessage(chatId, 
                "An error occurred while processing your command.", 
                cancellationToken: cancellationToken);
        }
    }

    private Task HandleEditedMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Received edited message from {ChatId}", message.Chat.Id);
        return Task.CompletedTask;
    }

    // ==================== SESSION COMMAND HANDLERS ====================
    
    private async Task HandleStartSessionAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var userId = message.From!.Id;
        var displayName = message.From.GetDisplayName();

        // Check if request was done for a group chat:
        var chat = await botClient.GetChat(chatId);
        if (chat.Type != ChatType.Group)
        {
            await botClient.SendMessage(chatId, 
                "❌ Sessions can only be started in group chats.",
                cancellationToken: cancellationToken);
            return;
        }

        // Check if only admins can start sessions
        if (!_workflowService.Options.AllowNonAdminSessionStart)
        {
            if (!await IsUserAdminAsync(botClient, chatId, userId, cancellationToken))
            {
                await botClient.SendMessage(chatId, 
                    "❌ Only group administrators can start a session.", 
                    cancellationToken: cancellationToken);
                return;
            }
        }

        if (_workflowService.TryStartSession(chat, userId, displayName, out var session))
        {
            var startMsg = await botClient.SendMessage(chatId,
                $"🎯 *Session Started!*\n\n" +
                $"Started by: {displayName}\n\n" +
                $"Everyone can now make payments! Send amounts like:\n" +
                $"• `3,99` (€ per default)\n" +
                $"• `5,50eur` or `5€`\n" + // TODO: no samples here
                $"• `2eur+1000sat`\n\n" +
                $"Use /closesession to close and start the lottery!",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);
            
            session.StartMessageId = startMsg.MessageId;
            
            try
            {
                await MessageHelper.SendPinnedStatusAsync(session, botClient, _workflowService, cancellationToken);
                _logger.LogInformation("Session started in chat {ChatId} by user {UserId}", chatId, userId);
            }
            catch (Exception)
            {
                await botClient.DeleteMessage(chatId, startMsg.MessageId, cancellationToken);
            }
        }
        else
        {
            await botClient.SendMessage(chatId,
                "⚠️ A session is already active in this group!",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleCloseSessionAsync(ITelegramBotClient botClient, long chatId, long userId, CancellationToken cancellationToken)
    {
        // Check permissions
        if (!_workflowService.Options.AllowNonAdminSessionClose)
        {
            if (!await IsUserAdminAsync(botClient, chatId, userId, cancellationToken))
            {
                await botClient.SendMessage(chatId,
                    "❌ Only group administrators can close a session.",
                    cancellationToken: cancellationToken);
                return;
            }
        }

        var session = _workflowService.GetSessionByChat(chatId);
        if (session is null)
        {
            await botClient.SendMessage(chatId,
                "⚠️ No active session in this group.",
                cancellationToken: cancellationToken);
            return;
        }

        if (session.Phase > SessionPhase.AcceptingPayments)
        {
            await botClient.SendMessage(chatId,
                "⚠️ Session has already moved past the payment phase.",
                cancellationToken: cancellationToken);
            return;
        }

        if (!session.HasPayments)
        {
            await HandleCancelSessionAsync(botClient, chatId, userId, cancellationToken);
            return;
        }

        // Check if anyone entered the lottery
        if (session.LotteryParticipants.Count == 0)
        {
            await botClient.SendMessage(chatId,
                "❌ No one entered the lottery. Session cancelled.",
                cancellationToken: cancellationToken);

            _workflowService.TryCloseSession(chatId);
        }
        else
        {
            // Draw winner immediately
            var participants = session.LotteryParticipants.ToArray();
            var winnerUserId = participants[Random.Shared.Next(participants.Length)];
            
            session.WinnerUserId = winnerUserId;
            session.Phase = SessionPhase.WaitingForInvoice;

            var winner = session.Participants[winnerUserId];

            await MessageHelper.SendWinnerMessageAsync(session, botClient, _workflowService, cancellationToken);

            _logger.LogInformation("Winner selected immediately for chat {ChatId}: user {UserId}", chatId, winnerUserId);
        }
        
        await MessageHelper.UpdatePinnedStatusAsync(session, botClient, _workflowService, _logger, cancellationToken);
    }

    private async Task HandleCancelSessionAsync(ITelegramBotClient botClient, long chatId, long userId, CancellationToken cancellationToken)
    {
        var session = _workflowService.GetSessionByChat(chatId);
        if (session is null)
            return;

        // Check permissions
        if (session?.HasPayments == false)
            ; // Skip (No need to check)
        else if (!_workflowService.Options.AllowNonAdminSessionCancel)
        {
            if (!await IsUserAdminAsync(botClient, chatId, userId, cancellationToken))
            {
                await botClient.SendMessage(chatId,
                    "❌ Only group administrators can cancel a session.",
                    cancellationToken: cancellationToken);
                return;
            }
        }

        if (_workflowService.TryCloseSession(chatId))
        {
            await MessageHelper.UpdatePinnedStatusAsync(session!, botClient, _workflowService, _logger, cancellationToken);
        
            await botClient.SendMessage(chatId,
                "❌ Session has been cancelled and removed.",
                cancellationToken: cancellationToken);
            _logger.LogInformation("Session cancelled in chat {ChatId} by user {UserId}", chatId, userId);
        }
        else
        {
            await botClient.SendMessage(chatId,
                "⚠️ No active session to close.",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleStatusAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var session = _workflowService.GetSessionByChat(chatId);

        if (session is null)
        {
            await botClient.SendMessage(chatId,
                "ℹ️ No active session in this group.\n\nUse /startsession to start one!",
                cancellationToken: cancellationToken);
            return;
        }

        // Delete previous message (should exist, but we don't really know):
        if (session.StatusMessageId is not null)
            await botClient.DeleteMessage(message.Chat.Id, session.StatusMessageId!.Value);

        await MessageHelper.SendPinnedStatusAsync(session, botClient, _workflowService, cancellationToken);
    }

    // ==================== PAYMENT HANDLING ====================

    private async Task HandleDirectMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var userId = message.From!.Id;
        var displayName = message.From.GetDisplayName();
        var text = message.Text?.Trim();

        if (string.IsNullOrEmpty(text))
            return;

        // Check if this user is a winner waiting to submit an invoice
        foreach (var session in _sessionManager.ActiveSessions)
        {
            if (session.WinnerUserId == userId && 
                session.Phase == SessionPhase.WaitingForInvoice &&
                string.IsNullOrEmpty(session.WinnerInvoiceBolt11))
            {
                // This looks like an invoice submission
                if (text.StartsWith("ln", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessWinnerInvoiceAsync(botClient, session, userId, text, cancellationToken);
                    return;
                }
            }
        }

        // Try to parse as payment from session participant
        if (PaymentParser.TryParse(text, out var tokens, out var error))
        {
            // Find active session where this user is a participant
            var participantSession = _sessionManager.ActiveSessions
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
        // Process each token and create invoices
        foreach (var tokenGrp in tokens.GroupBy(t => t.Currency))
        {
            var grpAmount = (double)tokenGrp.Sum(tGrp => tGrp.Amount);
            var grpCurrency = tokenGrp.Key;
            try
            {
                var currencyName = grpCurrency.GetDescription();
                var memo = $"{session.ChatTitle}/{displayName} zapped";
                // TODO: pass the enum here, not a currency string! 
                var invoice = await _lnbitsService.CreateInvoiceAsync(grpAmount, currencyName, memo, cancellationToken).ConfigureAwait(false);

                if (invoice is null)
                {
                    await botClient.SendMessage(userId,
                        $"❌ Failed to create invoice for {grpAmount} {currencyName}",
                        cancellationToken: cancellationToken);
                    continue;
                }

                // Store as pending payment
                var pending = new PendingPayment
                {
                    PaymentHash = invoice.PaymentHash,
                    PaymentRequest = invoice.PaymentRequest,
                    UserId = userId,
                    DisplayName = displayName,
                    Amount = (decimal)grpAmount,
                    Currency = grpCurrency,
                    InputExpression = inputExpression,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                session.PendingPayments.TryAdd(invoice.PaymentHash, pending);

                var message = await MessageHelper.SendPaymentMessageAsync(pending, botClient, cancellationToken).ConfigureAwait(false);
                pending.MessageId = message.MessageId;

                _logger.LogInformation("Created invoice for user {UserId} in session {ChatId}: {Amount} {Currency}",
                    userId, session.ChatId, grpAmount, grpCurrency);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating invoice for private payment");
                await botClient.SendMessage(userId, $"❌ Error creating invoice for {grpAmount} {grpCurrency}. Please try again.", cancellationToken: cancellationToken);
            }
        }
    }

    // ==================== CALLBACK QUERY HANDLING ====================

    private async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        try
        {
            await botClient.AnswerCallbackQuery(query.Id, cancellationToken: cancellationToken);

            var data = query.Data;
            if (string.IsNullOrEmpty(data))
                return;

            var chatId = query.Message!.Chat.Id;
            var userId = query.From.Id;
            var displayName = query.From.GetDisplayName();

            switch (data)
            {
                case CallbackActions.JoinLottery:
                    await HandleJoinLotteryAsync(botClient, chatId, userId, displayName, query.Message.MessageId, cancellationToken);
                    break;

                case CallbackActions.ViewStatus:
                    await HandleViewStatusCallbackAsync(botClient, chatId, cancellationToken);
                    break;

                case CallbackActions.SubmitInvoice:
                    await HandleSubmitInvoiceCallbackAsync(botClient, chatId, userId, cancellationToken);
                    break;

                case CallbackActions.JoinSession:
                    await HandleJoinSessionAsync(botClient, chatId, userId, displayName, query.Message.MessageId, cancellationToken);
                    break;

                case CallbackActions.CloseSession:
                    await HandleCloseSessionAsync(botClient, chatId, userId, cancellationToken);
                    break;

                case CallbackActions.CancelSession:
                    await HandleCancelSessionAsync(botClient, chatId, userId, cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling callback query");
        }
    }

    private async Task HandleJoinSessionAsync(ITelegramBotClient botClient, long chatId, long userId, string displayName, int messageId, CancellationToken cancellationToken)
    {
        var session = _workflowService.GetSessionByChat(chatId);
        if (session is null || session.Phase > SessionPhase.AcceptingPayments)
        {
            await botClient.SendMessage(chatId, "⚠️ Session is not currently accepting new participants.", cancellationToken: cancellationToken);
            return;
        }

        // Check if user is already a participant
        if (session.Participants.ContainsKey(userId))
        {
            await botClient.SendMessage(chatId,
                $"ℹ️ {displayName}, you're already part of this session!",
                cancellationToken: cancellationToken);
            return;
        }

        // Add user as participant
        _workflowService.EnsureParticipant(session, userId, displayName);

        // Send private welcome message
        try
        {
            var chat = await botClient.GetChat(session.ChatId, cancellationToken);

            var lotteryButton = new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
                Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("🎰 Enter Lottery", CallbackActions.JoinLottery));

            await botClient.SendMessage(userId,
                $"🎉 Welcome to the *{chat.Title}* Team Zaps session!\n\n" +
                $"💰 **Make payments** by sending amounts like:\n" +
                $"• `3,99` (€ per default)\n" +
                $"• `5,50eur` or `5€`\n" +
                $"• `2eur+1000sat`\n" +
                $"I'll create Lightning invoices for you to pay.\n\n" +
                $"🎰 Feel free to **enter the lottery** if you're willing to pay the fiat bill if you win. In return, you'll receive all the sats collected from everyone!\n\n" +
                $"⚠️ **Payments are blocked** until someone enters the lottery first!",
                parseMode: ParseMode.Markdown,
                replyMarkup: lotteryButton,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send private welcome message to user {UserId}", userId);
            await botClient.SendMessage(chatId,
                $"⚠️ {displayName}, please start a private chat with me first by clicking @{(await botClient.GetMe(cancellationToken)).Username}",
                cancellationToken: cancellationToken);
        }

        // Update the pinned status message
        await MessageHelper.UpdatePinnedStatusAsync(session, botClient, _workflowService, _logger, cancellationToken);

        _logger.LogInformation("User {UserId} joined session in chat {ChatId}", userId, chatId);
    }

    private async Task HandleJoinLotteryAsync(ITelegramBotClient botClient, long chatId, long userId, string displayName, int messageId, CancellationToken cancellationToken)
    {
        var session = _workflowService.GetSessionByUser(userId);
        if (session is null)
        {
            await botClient.SendMessage(chatId, "⚠️ No active session found.", cancellationToken: cancellationToken);
            return;
        }

        // Handle different phases
        if (session.Phase == SessionPhase.WaitingForLotteryParticipants)
        {
            // First lottery participant - unlock payments
            if (session.LotteryParticipants.Add(userId))
            {
                session.Phase = SessionPhase.AcceptingPayments;
                
                await botClient.SendMessage(chatId,
                    $"🎉 {displayName} entered the lottery! Payments are now unlocked for everyone! 💰",
                    cancellationToken: cancellationToken);

                await MessageHelper.UpdatePinnedStatusAsync(session, botClient, _workflowService, _logger, cancellationToken);
                
                _logger.LogInformation("First lottery participant {UserId} in chat {ChatId}, payments unlocked", userId, chatId);
            }
            else
            {
                await botClient.SendMessage(chatId,
                    $"ℹ️ {displayName}, you've already entered the lottery!",
                    cancellationToken: cancellationToken);
            }
            return;
        }
        else if (session.Phase == SessionPhase.AcceptingPayments)
        {
            // More people can still join lottery during payment phase
            if (session.LotteryParticipants.Add(userId))
            {
                await botClient.SendMessage(chatId,
                    $"✅ {displayName} entered the lottery! 🎟️",
                    cancellationToken: cancellationToken);

                await MessageHelper.UpdatePinnedStatusAsync(session, botClient, _workflowService, _logger, cancellationToken);
                
                _logger.LogInformation("User {UserId} joined lottery in chat {ChatId}", userId, chatId);
            }
            else
            {
                await botClient.SendMessage(chatId,
                    $"ℹ️ {displayName}, you've already entered the lottery!",
                    cancellationToken: cancellationToken);
            }
            return;
        }

        // Phase not valid for lottery joining
        await botClient.SendMessage(chatId, 
            $"⚠️ Lottery joining is not available in current phase: {session.Phase}", 
            cancellationToken: cancellationToken);
    }

    private async Task HandleViewStatusCallbackAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        var session = _workflowService.GetSessionByChat(chatId);
        if (session is null)
        {
            await botClient.SendMessage(chatId, "⚠️ No active session.", cancellationToken: cancellationToken);
            return;
        }

        await MessageHelper.SendPinnedStatusAsync(session, botClient, _workflowService, cancellationToken);
    }

    private async Task HandleSubmitInvoiceCallbackAsync(ITelegramBotClient botClient, long chatId, long userId, CancellationToken cancellationToken)
    {
        var session = _workflowService.GetSessionByChat(chatId);
        if (session is null || session.WinnerUserId != userId)
        {
            await botClient.SendMessage(chatId, "⚠️ You are not the lottery winner.", cancellationToken: cancellationToken);
            return;
        }

        await botClient.SendMessage(chatId,
            $"🏆 Please send me your Lightning invoice in a *private message*!\n\n" +
            $"Amount: *{_workflowService.TotalSats(session)} sats*\n\n" +
            $"Click here to message me: @{(await botClient.GetMe(cancellationToken)).Username}",
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }



    private async Task ProcessWinnerInvoiceAsync(ITelegramBotClient botClient, SessionState session, long userId, string bolt11, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Winner invoice submitted for session in chat {ChatId}", session.ChatId);

        session.WinnerInvoiceBolt11 = bolt11;
        session.InvoiceSubmittedAt = DateTimeOffset.UtcNow;

        await botClient.SendMessage(userId,
            "✅ Invoice received! Processing payout...",
            cancellationToken: cancellationToken);

        // Execute payout
        await ExecutePayoutAsync(botClient, session, cancellationToken);
    }

    private async Task ExecutePayoutAsync(ITelegramBotClient botClient, SessionState session, CancellationToken cancellationToken)
    {
        if (session.PayoutCompleted)
            return;

        try
        {
            var paymentResult = await _lnbitsService.PayInvoiceAsync(session.WinnerInvoiceBolt11!, cancellationToken);
            
            if (paymentResult is not null)
            {
                session.Phase = SessionPhase.Closed;
                session.PayoutCompleted = true;
                session.PayoutExecutedAt = DateTimeOffset.UtcNow;

                await MessageHelper.UpdateWinnerMessageAsync(session, PaymentStatus.Paid, paymentResult, botClient, _workflowService, _logger, cancellationToken);

                // Update the pinned status message
                await MessageHelper.UpdatePinnedStatusAsync(session, botClient, _workflowService, _logger, cancellationToken);
                
                // Clean up session
                _workflowService.TryCloseSession(session.ChatId);

                _logger.LogInformation("Payout executed successfully for chat {ChatId}", session.ChatId);
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
            _logger.LogError(ex, "Error executing payout for chat {ChatId}", session.ChatId);
            await botClient.SendMessage(session.ChatId,
                "❌ Error during payout. Please contact support.",
                cancellationToken: cancellationToken);
        }
    }

    // ==================== HELPER METHODS ====================

    private async Task<bool> IsUserAdminAsync(ITelegramBotClient botClient, long chatId, long userId, CancellationToken cancellationToken)
    {
        try
        {
            var member = await botClient.GetChatMember(chatId, userId, cancellationToken); // TODO: es fehlt oft `configureAwait(false)`...
            return member.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator;
        }
        catch
        {
            return false;
        }
    }

    private string BuildSessionSummary(SessionSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("📋 *Session Summary*\n");
        sb.AppendLine($"Duration: {(summary.EndedAt - summary.StartedAt).TotalMinutes:F0} minutes");
        sb.AppendLine($"Total collected: *{summary.TotalCollectedSats} sats*");
        sb.AppendLine($"Participants: *{summary.ParticipantCount}*");
        
        if (summary.WinnerDisplayName is not null)
        {
            sb.AppendLine($"\n🏆 Winner: *{summary.WinnerDisplayName}*");
            sb.AppendLine($"Payout completed: {(summary.PayoutCompleted ? "✅ Yes" : "❌ No")}");
        }

        return sb.ToString();
    }
}

// ==================== EXTENSION METHODS ====================

internal static class UserExtensions
{
    public static string GetDisplayName(this User user)
    {
        if (!string.IsNullOrEmpty(user.Username))
            return $"@{user.Username}";
        
        var name = user.FirstName;
        if (!string.IsNullOrEmpty(user.LastName))
            name += $" {user.LastName}";
        
        return name;
    }
}

internal static partial class Ext
{
    public static bool IsCommand(this Message source) => (source.Text?.StartsWith('/') == true);
    public static void GetCommand(this Message source, out string command, out string[] args)
    {
        if (source?.IsCommand() == true)
        {
            var items = source.Text!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            {
                command = items.First().ToLower();
                args = items.Skip(1).ToArray();
            }
        }
        else
            throw new InvalidOperationException("Failed to parse command from message!");
    }

    /// <inheritdoc cref="GetArgument(string[], int)"/>
    public static T GetArgument<T>(this string[] source, int index) => (T)Convert.ChangeType(GetArgument(source, index), typeof(T));
    /// <summary>
    /// Gets the argument at the specified index.
    /// </summary>
    public static string GetArgument(this string[] source, int index)
    {
        var res = TryGetArgument(source, index);
        if (res is null)
            throw new IndexOutOfRangeException("Argument index out of range.");
        else
            return (res!);
    }
    /// <summary>
    /// Gets the argument at the specified index and all following arguments as concatenated string.
    /// </summary>
    public static string GetArguments(this string[] source, int index)
    {
        var res = TryGetArguments(source, index);
        if (res is null)
            throw new IndexOutOfRangeException("Argument index out of range.");
        else
            return (res!);
    }
    /// <inheritdoc cref="GetArgument(string[], int)"/>
    public static T? TryGetArgument<T>(this string[] source, int index)
        where T : IConvertible
    {
        var res = TryGetArgument(source, index);
        if (res is null)
            return (default);
        else
            return ((T)Convert.ChangeType(res, typeof(T)));
    }
    /// <inheritdoc cref="GetArgument(string[], int)"/>
    public static string? TryGetArgument(this string[] source, int index)
    {
        if (index < source.Length)
            return (source.ElementAt(index));
        else
            return (null);
    }
    /// <inheritdoc cref="GetArguments(string[], int)"/>
    public static string? TryGetArguments(this string[] source, int index)
    {
        if (index < source.Length)
            return (string.Join(' ', source.Skip(index)));
        else
            return (null);
    }
}
