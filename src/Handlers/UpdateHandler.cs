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
                        "/endsession - End payments and start lottery\n" +
                        "/status - View session details\n\n" +
                        "*How it works:*\n" +
                        "1️⃣ Join the session using the button on the pinned message\n" +
                        "2️⃣ Send me payment amounts in *private chat*:\n" +
                        "   • `1000` (sats)\n" +
                        "   • `5eur` or `5€`\n" +
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

                case "/endsession":
                    await HandleStopSessionAsync(botClient, message, cancellationToken);
                    break;

                case "/closesession":
                    await HandleCloseSessionAsync(botClient, message, cancellationToken);
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
                $"• `1000` (sats)\n" +
                $"• `5eur` or `5€`\n" + // TODO: no samples here
                $"• `2eur+1000sat`\n\n" +
                $"Use /endsession to end and start the lottery!",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken);
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

    private async Task HandleStopSessionAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var userId = message.From!.Id;

        // Check permissions
        if (!_workflowService.Options.AllowNonAdminSessionStop)
        {
            if (!await IsUserAdminAsync(botClient, chatId, userId, cancellationToken))
            {
                await botClient.SendMessage(chatId,
                    "❌ Only group administrators can stop a session.",
                    cancellationToken: cancellationToken);
                return;
            }
        }

        var session = _workflowService.GetSession(chatId);
        if (session is null)
        {
            await botClient.SendMessage(chatId,
                "⚠️ No active session in this group.",
                cancellationToken: cancellationToken);
            return;
        }

        if (session.Phase != SessionPhase.AcceptingPayments)
        {
            await botClient.SendMessage(chatId,
                "⚠️ Session has already moved past the payment phase.",
                cancellationToken: cancellationToken);
            return;
        }

        var totalSats = _workflowService.TotalSats(session);
        if (totalSats == 0)
        {
            await HandleCloseSessionAsync(botClient, message, cancellationToken);
            return;
        }

        // Move to lottery phase
        session.Phase = SessionPhase.LotteryOpen;
        session.LotteryOpenedAt = DateTimeOffset.UtcNow;
        session.LotteryClosesAt = DateTimeOffset.UtcNow.Add(_workflowService.Options.LotteryJoinWindow);

        var keyboard = _workflowService.BuildLotteryKeyboard(session, false);
        var msg = await botClient.SendMessage(chatId,
            $"🎰 *Lottery Started!*\n\n" +
            $"Total pot: *{totalSats} sats*\n" +
            $"Participants: *{session.Participants.Count}*\n\n" +
            $"Click the button below to join the lottery!\n" +
            $"You have *{_workflowService.Options.LotteryJoinWindow.TotalMinutes:F0} minutes* to join.\n\n" +
            $"One winner will be drawn to receive all the sats! 🎉",
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);

        session.LotteryMessageId = msg.MessageId;
        
        _logger.LogInformation("Lottery started in chat {ChatId}, total {TotalSats} sats", chatId, totalSats);
    }

    private async Task HandleCloseSessionAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var userId = message.From!.Id;

        // Check permissions
        if (!_workflowService.Options.AllowNonAdminSessionClose)
        {
            if (!await IsUserAdminAsync(botClient, chatId, userId, cancellationToken))
            {
                await botClient.SendMessage(chatId,
                    "❌ Only group administrators can force-close a session.",
                    cancellationToken: cancellationToken);
                return;
            }
        }

        if (_workflowService.TryCloseSession(chatId))
        {
            await botClient.SendMessage(chatId,
                "✅ Session has been closed and removed.",
                cancellationToken: cancellationToken);
            _logger.LogInformation("Session force-closed in chat {ChatId} by user {UserId}", chatId, userId);
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
        var session = _workflowService.GetSession(chatId);

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

    private async Task HandlePaymentMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var chatTitle = message.Chat.Title;
        var session = _workflowService.GetSession(chatId);

        if (session is null || session.Phase != SessionPhase.AcceptingPayments)
            return; // Silently ignore if no session or wrong phase

        var text = message.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        // Try to parse as payment
        if (!PaymentParser.TryParse(text, out var tokens, out var error))
            return; // Not a payment message

        var userId = message.From!.Id;
        var displayName = message.From.GetDisplayName();

        // Check if user has joined the session
        if (!session.Participants.ContainsKey(userId))
        {
            await botClient.SendMessage(chatId,
                $"❌ {displayName}, you must join the session first! Use the button on the pinned message.",
                cancellationToken: cancellationToken);
            return;
        }

        // Inform user to use private messages for payments
        await botClient.SendMessage(chatId,
            $"💬 {displayName}, please send your payment amounts to me in a private message!\n" +
            $"Click here: @{(await botClient.GetMe(cancellationToken)).Username}",
            cancellationToken: cancellationToken);
        return;

        // Payment processing is now handled in private messages only
        // This method now just redirects users to private chat
    }

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
                .FirstOrDefault(s => s.Phase == SessionPhase.AcceptingPayments && s.Participants.ContainsKey(userId));

            if (participantSession is not null)
            {
                await ProcessPrivatePaymentAsync(botClient, participantSession, userId, displayName, tokens, text, cancellationToken);
                return;
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
            "• Payment amounts (like `1000`, `5eur`, `2eur+1000sat`) if you're in a session\n" +
            "• Lightning invoice (BOLT11) if you won the lottery\n" +
            "• Use /help for commands",
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    private async Task ProcessPrivatePaymentAsync(ITelegramBotClient botClient, SessionState session, long userId, string displayName, List<PaymentToken> tokens, string inputExpression, CancellationToken cancellationToken)
    {
        // Process each token and create invoices
        foreach (var token in tokens)
        {
            try
            {
                var currency = token.Currency.GetDescription();
                var invoice = await _lnbitsService.CreateInvoiceAsync((double)token.Amount, currency, // TODO: pass the enum here, not a string!
                    $"{session.ChatTitle}/{displayName} zapped", cancellationToken);

                if (invoice is null)
                {
                    await botClient.SendMessage(userId,
                        $"❌ Failed to create invoice for {token.Amount} {currency}",
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
                    Amount = token.Amount,
                    Currency = token.Currency,
                    InputExpression = inputExpression,
                    CreatedAt = DateTimeOffset.UtcNow
                };

                session.PendingPayments.TryAdd(invoice.PaymentHash, pending);

                await botClient.SendMessage(userId,
                    $"⚡ *Invoice Created*\n\n" +
                    $"Amount: `{token.Amount} {currency}`\n\n" +
                    $"`{invoice.PaymentRequest}`\n\n" +
                    $"Pay this invoice to add your contribution to the session!",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Created invoice for user {UserId} in session {ChatId}: {Amount} {Currency}",
                    userId, session.ChatId, token.Amount, currency);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating invoice for private payment");
                await botClient.SendMessage(userId,
                    $"❌ Error creating invoice for {token.Amount} {token.Currency.GetDescription()}. Please try again.",
            cancellationToken: cancellationToken);
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

                case CallbackActions.VotePayout:
                    await HandleVotePayoutAsync(botClient, chatId, userId, displayName, cancellationToken);
                    break;

                case CallbackActions.JoinSession:
                    await HandleJoinSessionAsync(botClient, chatId, userId, displayName, query.Message.MessageId, cancellationToken);
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
        var session = _workflowService.GetSession(chatId);
        if (session is null || session.Phase != SessionPhase.AcceptingPayments)
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

            await botClient.SendMessage(userId,
                $"🎉 Welcome to the *{chat.Title}* Team Zaps session!\n\n" +
                $"You can now make payments by sending amounts like:\n" +
                $"• `1000` (sats)\n" +
                $"• `5eur` or `5€`\n" +
                $"• `2eur+1000sat`\n\n" +
                $"I'll create Lightning invoices for you to pay. Once everyone is done, we'll start the lottery! 🎰",
                parseMode: ParseMode.Markdown,
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
        var session = _workflowService.GetSession(chatId);
        if (session is null || session.Phase != SessionPhase.LotteryOpen)
        {
            await botClient.SendMessage(chatId, "⚠️ Lottery is not currently open.", cancellationToken: cancellationToken);
            return;
        }

        // Check if user made any payments
        if (!session.Participants.ContainsKey(userId))
        {
            await botClient.SendMessage(chatId,
                $"❌ {displayName}, you must make a payment to join the lottery!",
                cancellationToken: cancellationToken);
            return;
        }

        // Add to lottery
        if (session.LotteryParticipants.Add(userId))
        {
            await botClient.SendMessage(chatId,
                $"✅ {displayName} joined the lottery! 🎟️",
                cancellationToken: cancellationToken);

            // Update the lottery message
            var keyboard = _workflowService.BuildLotteryKeyboard(session, true);
            var totalSats = _workflowService.TotalSats(session);
            
            await botClient.EditMessageText(
                chatId: chatId,
                messageId: messageId,
                text: $"🎰 *Lottery Started!*\n\n" +
                      $"Total pot: *{totalSats} sats*\n" +
                      $"Participants: *{session.Participants.Count}*\n" +
                      $"Lottery entries: *{session.LotteryParticipants.Count}*\n\n" +
                      $"Click the button below to join the lottery!\n" +
                      $"Time remaining: Check back soon!\n\n" +
                      $"One winner will be drawn to receive all the sats! 🎉",
                parseMode: ParseMode.Markdown,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);

            _logger.LogInformation("User {UserId} joined lottery in chat {ChatId}", userId, chatId);
        }
        else
        {
            await botClient.SendMessage(chatId,
                $"ℹ️ {displayName}, you've already joined the lottery!",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleViewStatusCallbackAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        var session = _workflowService.GetSession(chatId);
        if (session is null)
        {
            await botClient.SendMessage(chatId, "⚠️ No active session.", cancellationToken: cancellationToken);
            return;
        }

        await MessageHelper.SendPinnedStatusAsync(session, botClient, _workflowService, cancellationToken);
    }

    private async Task HandleSubmitInvoiceCallbackAsync(ITelegramBotClient botClient, long chatId, long userId, CancellationToken cancellationToken)
    {
        var session = _workflowService.GetSession(chatId);
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

    private async Task HandleVotePayoutAsync(ITelegramBotClient botClient, long chatId, long userId, string displayName, CancellationToken cancellationToken)
    {
        var session = _workflowService.GetSession(chatId);
        if (session is null || session.Phase != SessionPhase.WaitingForPayout)
        {
            await botClient.SendMessage(chatId, "⚠️ No payout is pending.", cancellationToken: cancellationToken);
            return;
        }

        // Check if user participated
        if (!session.Participants.ContainsKey(userId))
        {
            await botClient.SendMessage(chatId, "❌ Only participants can vote.", cancellationToken: cancellationToken);
            return;
        }

        if (session.PayoutVotes.Add(userId))
        {
            var totalVoters = session.Participants.Count;
            var votesNeeded = (int)Math.Ceiling(totalVoters * _workflowService.Options.EarlyPayoutVoteThreshold);
            var currentVotes = session.PayoutVotes.Count;

            await botClient.SendMessage(chatId,
                $"✅ {displayName} voted for immediate payout!\n\n" +
                $"Votes: {currentVotes}/{votesNeeded}",
                cancellationToken: cancellationToken);

            // Check if threshold reached
            if (currentVotes >= votesNeeded)
            {
                await ExecutePayoutAsync(botClient, session, cancellationToken);
            }

            _logger.LogInformation("User {UserId} voted for payout in chat {ChatId} ({CurrentVotes}/{VotesNeeded})",
                userId, chatId, currentVotes, votesNeeded);
        }
        else
        {
            await botClient.SendMessage(chatId, "ℹ️ You've already voted!", cancellationToken: cancellationToken);
        }
    }

    private async Task ProcessWinnerInvoiceAsync(ITelegramBotClient botClient, SessionState session, long userId, string bolt11, CancellationToken cancellationToken)
    {
        session.WinnerInvoiceBolt11 = bolt11;
        session.InvoiceSubmittedAt = DateTimeOffset.UtcNow;
        session.Phase = SessionPhase.WaitingForPayout;
        session.PayoutScheduledAt = DateTimeOffset.UtcNow.Add(_workflowService.Options.PayoutDelay);

        await botClient.SendMessage(userId,
            "✅ Invoice received! The payout will be processed soon.",
            cancellationToken: cancellationToken);

        var keyboard = _workflowService.BuildPayoutKeyboard(session, userId);
        var msg = await botClient.SendMessage(session.ChatId,
            $"🎉 *Winner submitted invoice!*\n\n" +
            $"Payout scheduled for: *{session.PayoutScheduledAt:yyyy-MM-dd HH:mm} UTC*\n\n" +
            $"Want to pay out now? Vote below!\n" +
            $"Need {(int)(session.Participants.Count * _workflowService.Options.EarlyPayoutVoteThreshold)} votes.",
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);

        session.CurrentPayoutActionMessageId = msg.MessageId;

        _logger.LogInformation("Winner invoice submitted for session in chat {ChatId}", session.ChatId);
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
                session.PayoutCompleted = true;
                session.PayoutExecutedAt = DateTimeOffset.UtcNow;

                await botClient.SendMessage(session.ChatId,
                    $"⚡ *Payout Completed!*\n\n" +
                    $"Amount: *{paymentResult.Amount} sats*\n" +
                    $"Fee: *{paymentResult.Fee} sats*\n\n" +
                    $"Thank you for using Team Zaps! 🎉",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken);

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
