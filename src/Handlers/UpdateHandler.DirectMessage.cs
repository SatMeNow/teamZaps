using System.Diagnostics;
using System.Numerics;
using System.Text;
using TeamZaps;
using TeamZaps.Backends;
using TeamZaps.Configuration;
using TeamZaps.Logging;
using TeamZaps.Payment;
using TeamZaps.Services;
using TeamZaps.Session;
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
                    .AppendLine("*Welcome to TeamZaps!* 🎯")
                    .AppendLine()
                    .AppendLine("I help groups split bills using Bitcoin Lightning!")
                    .AppendLine()
                    .AppendLine("*How it works:*")
                    .AppendLine("1️⃣ Someone *starts a session* in your group")
                    .AppendLine("2️⃣ *Join the session*")
                    .AppendLine("3️⃣ *Send me your payments*")
                    .AppendLine("4️⃣ One *random participant wins the pot*!")
                    .AppendLine()
                    .AppendLine("*Nice to know:*")
                    .AppendLine("💡 *Your sats are safe* with me! Your can [recover lost sats](https://github.com/SatMeNow/teamZaps/blob/master/README.MD#-lost-and-found-recovery) in case of any problems!")
                    .AppendLine()
                    .Append($"Use {BotPmCommand.Help} for detailed instructions.");
                if (pendingJoin is not null)
                    welcomeMessage.AppendLine("\n\n💡 Okay, let's continue and join the session 🏃‍➡️");
                await botClient.SendMessage(command.ChatId, 
                    welcomeMessage.ToString(),
                    parseMode: ParseMode.Markdown,
                    linkPreviewOptions: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                // Send private status message if pending
                if (pendingJoin is not null)
                {
                    await HandleJoinSessionAsync(botClient, pendingJoin.ChatId, command.Source.From!, cancellationToken).ConfigureAwait(false);

                    // Update or delete the shared group welcome message
                    var joinedSession = workflowService.TryGetSessionByChat(pendingJoin.ChatId);
                    if (joinedSession?.PendingWelcome is not null)
                    {
                        joinedSession.PendingWelcome!.PendingUsers.RemoveAll(u => u.Id == command.UserId);
                        if (joinedSession.PendingWelcome?.PendingUsers!.IsEmpty() == true)
                            await WelcomeMessage.DeleteAsync(joinedSession, botClient, cancellationToken).ConfigureAwait(false);
                        else
                            await WelcomeMessage.UpdateAsync(joinedSession, botClient, cancellationToken).ConfigureAwait(false);
                    }
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
                    "ℹ️ For *detailed info*, check out the [user documents](https://github.com/SatMeNow/teamZaps/blob/master/README.MD).",
                    parseMode: ParseMode.Markdown,
                    linkPreviewOptions: true,
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
                    .AppendLine()
                    .AppendLine("📞 *Support:*")
                    .AppendLine("• Refer to the [support documents](https://github.com/SatMeNow/teamZaps/blob/master/README.MD#-support) for further instructions.")
                    .AppendLine("• Consider also to create an [issue](https://github.com/SatMeNow/teamZaps/issues) for bugs or feature requests")
                    .AppendLine()
                    .AppendLine("🧑‍💻 *Open Source:*")
                    .AppendLine("• [TeamZaps repository](https://github.com/SatMeNow/teamZaps) on GitHub")
                    .AppendLine("• [User documents](https://github.com/SatMeNow/teamZaps/blob/master/README.MD)")
                    .AppendLine("• [Developer documents](https://github.com/SatMeNow/teamZaps/blob/master/src/README.md)")
                    .AppendLine()
                    .AppendLine("💜 *Support the Project:*")
                    .AppendLine("• [Value for value](https://github.com/SatMeNow/teamZaps/blob/master/README.MD#-support-the-project)")
                    .AppendLine()
                    .Append($"Use {BotPmCommand.Help} for commands and instructions.");
                
                await botClient.SendMessage(command.ChatId,
                    aboutMessage.ToString(),
                    parseMode: ParseMode.Markdown,
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

        var session = sessionManager.TryGetSessionByUser(user.Id);
        if (session is null)
        {
            // Check if this is a recovery invoice
            if (text.IsLightningInvoice(out var recoveryInvoice))
            {
                var lostSats = await recoveryService.TryGetLostSatsAsync(user.Id).ConfigureAwait(false);
                if (lostSats is null)
                    throw new InvalidOperationException("Are you looking for *lost sats to recover*?\nSorry, there aren't any 🤷‍♂️")
                        .AddLogLevel(LogLevel.Information)
                        .AnswerUser();
                else
                {
                    await ProcessRecoveryInvoiceAsync(botClient, user, recoveryInvoice, lostSats, cancellationToken).ConfigureAwait(false);
                    return (true);
                }
            }
            return (false);
        }
        else
        {
            // Check for invoice submission:
            if (text.IsLightningInvoice(out var invoice))
            {
                switch (session.Phase)
                {
                    case SessionPhase.WaitingForInvoice:
                        await ProcessWinnerInvoiceAsync(botClient, session, user, invoice!, cancellationToken).ConfigureAwait(false);
                        return (true);

                    default:
                        throw new InvalidOperationException($"Invoices are not available in current session phase `{session.Phase.GetDescription()}`.")
                            .AddLogLevel(LogLevel.Warning)
                            .AnswerUser();
                }
            }
            // Check for payment:
            else if (PaymentParser.TryParse(text, out var tokens, out var error))
            {
                switch (session.Phase)
                {
                    case SessionPhase.WaitingForLotteryParticipants:
                        throw new InvalidOperationException("Orders are blocked until someone enters the lottery!\n\n" +
                            "Use the 🎰 Enter Lottery button in your welcome message or ask someone to enter the lottery first.")
                            .AddLogLevel(LogLevel.Warning)
                            .AnswerUser();

                    case SessionPhase.AcceptingOrders:
                        var participant = session.Participants.TryGetValue(user.Id);
                        if (participant?.PendingEdit is null)
                            // User placed a new order:
                            await ProcessPrivateOrderAsync(botClient, session, user, tokens, text, cancellationToken).ConfigureAwait(false);
                        else
                            // User edited an order:
                            await HandleApplyEditAsync(botClient, session, user, participant, tokens, cancellationToken).ConfigureAwait(false);
                        return (true);

                    default:
                        throw new InvalidOperationException($"Orders are not available in current session phase `{session.Phase.GetDescription()}`.")                            .AddLogLevel(LogLevel.Warning)
                            .AnswerUser();
                }
            }
            // Unknown input:
            else
                throw new NotImplementedException($"Sorry, push the `payment` button for instructions or use {BotPmCommand.Help} for commands.")
                    .AnswerUser();
        }
    }
    
    private async Task HandleJoinLotteryAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        workflowService.GetSessionParticipant(user.Id, out var session, out var participant);

        // Check if user already joined the lottery
        if (session.LotteryParticipants.ContainsKey(participant))
            throw new InvalidOperationException("You've already entered the lottery!")
                .AnswerUser();

        if (await botClient.DeleteMessageAsync(chatId, participant.BudgetSelectionMessageId, cancellationToken).ConfigureAwait(false))
            participant.BudgetSelectionMessageId = null;

        var availBudget = sessionManager.AvailableServerBudget;
        var keyboard = botBehaviour.Budget.Choices
            .Where(c => (availBudget is null) || (c <= availBudget!))
            .Select(c => InlineKeyboardButton.WithCallbackData($"{c}{BotBehaviorOptions.AcceptedFiatCurrency.ToSymbol()}", $"{CallbackActions.SelectBudget}_{c}"))
            .Chunk(4)
            .ToArray();

        var budgetMessage = await botClient.SendMessage(chatId, 
            "🎰 *Enter lottery* 🎰\n\n" +
            "How much are you willing to pay in fiat at maximum?\n\n" +
            "💡 *Multiple winners possible!* If total payments exceed your budget, " +
            "we'll select multiple winners to share the cost.", 
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        participant.BudgetSelectionMessageId = budgetMessage.MessageId;
    }
    private async Task HandleJoinLotteryAsync(ITelegramBotClient botClient, long chatId, User user, double budget, CancellationToken cancellationToken)
    {
        workflowService.GetSessionParticipant(user.Id, out var session, out var participant);

        // Check if user already joined
        if (session.LotteryParticipants.ContainsKey(participant))
            throw new InvalidOperationException("You've already entered the lottery.")
                .AddLogLevel(LogLevel.Information)
                .AnswerUser();

        // Check server-wide budget limit
        if (!CheckServerBudgetLimit(budget))
        {
            await liquidityLogService.LogAsync(LogTag.RejectJoinLottery, cancellationToken).ConfigureAwait(false);
            
            var availBudget = sessionManager.AvailableServerBudget!.Value;
            var minBudget = botBehaviour.Budget.Default;
            var message = $"Sorry, your budget of {budget.Format()} would exceed my total allowed liquidity 🫣\n\n" +
                $"Available at this time: {availBudget.Format()}";
            string help;
            if (minBudget <= availBudget)
                help = $"Please choose a lower budget and try again.";
            else
                help = $"Currently, no budgets are available to join the lottery. Please try again later.";
            throw new IndexOutOfRangeException(message)
                .AddHelp(help)
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();
        }

        // Add user to lottery with budget
        session.LotteryParticipants[participant] = budget;

        // Handle first lottery participant - unlock orders
        if (session.Phase == SessionPhase.WaitingForLotteryParticipants)
        {
            session.Phase = SessionPhase.AcceptingOrders;
            logger.LogInformation("First lottery participant {User} in chat {ChatId}, orders unlocked.", user, chatId);
        }
        else
            logger.LogInformation("User {User} joined lottery in chat {ChatId} with {Budget} budget.", user, chatId, budget.Format());
        
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        
        // Update user status messages for all participants
        await UpdateAllParticipantStatusesAsync(session, botClient, cancellationToken).ConfigureAwait(false);
    }
    private async Task HandleJoinLotteryWithBudgetAsync(ITelegramBotClient botClient, long chatId, User user, double budget, CancellationToken cancellationToken)
    {
        var userId = user.Id;
        if (workflowService.TryGetSessionByUser(userId, out var session))
        {
            var participant = session.Participants[userId];

            if (await botClient.DeleteMessageAsync(chatId, participant.BudgetSelectionMessageId, cancellationToken).ConfigureAwait(false))
                participant.BudgetSelectionMessageId = null;
                
            // Now process the lottery join
            await HandleJoinLotteryAsync(botClient, chatId, user, budget, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleTipSelectionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        workflowService.GetSessionParticipant(user.Id, out var session, out var participant);
             
        var keyboard = new InlineKeyboardMarkup(botBehaviour.Tip.Choices
            .Prepend((byte)0)
            .Select(t => InlineKeyboardButton.WithCallbackData(t.FormatTip(), $"{CallbackActions.SelectTip}_{t}"))
            .Chunk(4)
            .ToArray());
        var tipMessage = await botClient.SendMessage(chatId, 
            "🎩 *Setup tip*\n\n" +
            "Choose your *tip percentage* to be added to each payment:\n\n",
            //"💭 Tips help support Lightning Network infrastructure and development!"
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Store the tip selection message ID for cleanup
        participant.TipSelectionMessageId = tipMessage.MessageId;
    }

    private async Task HandleSetTipAsync(ITelegramBotClient botClient, long chatId, User user, int tip, CancellationToken cancellationToken)
    {
        workflowService.GetSessionParticipant(user.Id, out var session, out var participant);
        
        // Delete the tip selection message
        if (await botClient.DeleteMessageAsync(chatId, participant.TipSelectionMessageId, cancellationToken).ConfigureAwait(false))
            participant.TipSelectionMessageId = null;

        // Set the user's tip:
        participant.Options.Tip = (tip == 0) ? null : (byte)tip;
        // Save user options:
        await userOptionsService.WriteAsync(user.Id, participant.Options).ConfigureAwait(false);
        
        // Update the user's status message to reflect the new tip:
        await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
    }


    private async Task HandleLeaveSessionAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        workflowService.GetSessionParticipant(user.Id, out var session, out var participant);

        if (session.Phase > SessionPhase.AcceptingOrders)
            throw new InvalidOperationException("You can no longer leave the session at this stage.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();

        if (session.LotteryParticipants.TryGetValue(participant, out var lotteryBudget))
        {
            var othersOrders = session.OrdersFiatAmount - participant.OrdersFiatAmount;
            var newBudget = session.Budget - lotteryBudget;
            if (newBudget < othersOrders)
                throw new InvalidOperationException(
                    $"Sorry, you can't leave 🤷‍♂️\n\n" +
                    $"Removing your lottery budget of *{lotteryBudget.Format()}* would drop the total budget below the already ordered amounts of others.")
                    .AddLogLevel(LogLevel.Warning)
                    .AnswerUser()
                    .ExpireMessage();
        }
        workflowService.RemoveParticipant(session, user.Id);

        await botClient.SendMessage(chatId,
            $"✅ You have left the *{session.ChatTitle}* session.",
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken).ConfigureAwait(false);
            
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleShowEditPickerAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        workflowService.GetSessionParticipant(user.Id, out var session, out var participant);

        if (session.Phase != SessionPhase.AcceptingOrders)
            return;

        if (participant.EditPickerMessageId is not null)
            await EditOrderPickerMessage.UpdateAsync(participant, botClient, logger, cancellationToken).ConfigureAwait(false);
        else
            await EditOrderPickerMessage.SendAsync(participant, botClient, chatId, logger, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleEditTokenAsync(ITelegramBotClient botClient, long chatId, User user, int orderIndex, int tokenIndex, CancellationToken cancellationToken)
    {
        workflowService.GetSessionParticipant(user.Id, out var session, out var participant);

        if (session.Phase != SessionPhase.AcceptingOrders)
            return;

        // Guard against stale callbacks
        if (orderIndex < 0 || orderIndex >= participant.Orders.Count ||
            tokenIndex < 0 || tokenIndex >= participant.Orders[orderIndex].Tokens.Length)
            return;

        var token = participant.Orders[orderIndex].Tokens[tokenIndex];

        // Delete previous prompt if any
        if (participant.PendingEdit is { PromptMessageId: not null })
            await botClient.DeleteMessageAsync(chatId, participant.PendingEdit.PromptMessageId.Value, cancellationToken).ConfigureAwait(false);

        var prompt = await botClient.SendMessage(chatId,
            $"✏️ *Editing:* {token}\n\n" +
            $"Send the new amount and note, for example:\n`3,99 Beer`\n\n" +
            "_Tap ✖️ Close to cancel._",
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        participant.PendingEdit = new PendingEditToken(orderIndex, tokenIndex, prompt.MessageId);
    }

    private async Task HandleRemoveTokenAsync(ITelegramBotClient botClient, long chatId, User user, int orderIndex, int tokenIndex, CancellationToken cancellationToken)
    {
        workflowService.GetSessionParticipant(user.Id, out var session, out var participant);

        if (session.Phase != SessionPhase.AcceptingOrders)
            return;

        // Guard against stale callbacks
        if (orderIndex < 0 || orderIndex >= participant.Orders.Count ||
            tokenIndex < 0 || tokenIndex >= participant.Orders[orderIndex].Tokens.Length)
            return;

        var order = participant.Orders[orderIndex];
        if (order.Tokens.Length == 1)
        {
            participant.Orders.RemoveAt(orderIndex);
        }
        else
            order.RemoveToken(tokenIndex, participant.Options.Tip);

        await liquidityLogService.LogAsync(cancellationToken).ConfigureAwait(false);

        // Refresh or dismiss picker
        if (participant.HasOrders)
            await EditOrderPickerMessage.UpdateAsync(participant, botClient, logger, cancellationToken).ConfigureAwait(false);
        else
            await CloseEditPickerAsync(botClient, participant, cancellationToken).ConfigureAwait(false);

        await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCancelEditAsync(ITelegramBotClient botClient, long chatId, User user, CancellationToken cancellationToken)
    {
        if (!workflowService.TryGetSessionByUser(user.Id, out var session) ||
            !session.Participants.TryGetValue(user.Id, out var participant))
            return;

        if (participant.PendingEdit?.PromptMessageId is not null)
            await botClient.DeleteMessageAsync(chatId, participant.PendingEdit!.PromptMessageId!, cancellationToken).ConfigureAwait(false);
        participant.PendingEdit = null;

        await CloseEditPickerAsync(botClient, participant, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleApplyEditAsync(ITelegramBotClient botClient, SessionState session, User user, ParticipantState participant, List<PaymentToken> tokens, CancellationToken cancellationToken)
    {
        var pendingEdit = participant.PendingEdit!;

        if (tokens.Count != 1)
            throw new InvalidOperationException("Please send a *single item* to replace.\n\nExample: `4.50 Coffee`")
                .AnswerUser();

        // Validate indices (order may have been modified in the meantime)
        if (pendingEdit.OrderIndex >= participant.Orders.Count ||
            pendingEdit.TokenIndex >= participant.Orders[pendingEdit.OrderIndex].Tokens.Length)
        {
            participant.PendingEdit = null;
            return;
        }

        var newToken = tokens[0];
        var order = participant.Orders[pendingEdit.OrderIndex];
        var oldToken = order.Tokens[pendingEdit.TokenIndex];

        // Currency is immutable — only amount and note can change
        if (newToken.Currency != oldToken.Currency)
            throw new InvalidOperationException($"Currency cannot be changed! Only the amount and note can be updated.")
                .AnswerUser();

        // Remove the old token first (freeing up its budget share), then re-add via normal order flow
        if (order.Tokens.Length == 1)
            participant.Orders.RemoveAt(pendingEdit.OrderIndex);
        else
            order.RemoveToken(pendingEdit.TokenIndex, participant.Options.Tip);

        // Clear pending edit and delete prompt
        var promptMsgId = pendingEdit.PromptMessageId;
        participant.PendingEdit = null;
        if (promptMsgId is not null)
            await botClient.DeleteMessageAsync(participant.UserId, promptMsgId.Value, cancellationToken).ConfigureAwait(false);

        // Re-add as a new order — handles budget check, tip, confirmation message and status updates
        await ProcessPrivateOrderAsync(botClient, session, user, [newToken], newToken.Note ?? newToken.Amount.ToString(), cancellationToken).ConfigureAwait(false);

        await CloseEditPickerAsync(botClient, participant, cancellationToken).ConfigureAwait(false);
    }


    private async Task ProcessPrivateOrderAsync(ITelegramBotClient botClient, SessionState session, User user, List<PaymentToken> tokens, string inputExpression, CancellationToken cancellationToken)
    {
        var participant = session.Participants[user.Id];
        foreach (var tokenGrp in tokens.GroupBy(t => t.Currency))
        {
            var grpCurrency = tokenGrp.Key;

            // Ensure order to be in the accepted fiat currency only:
            if (grpCurrency != BotBehaviorOptions.AcceptedFiatCurrency)
                throw new NotSupportedException($"Only {BotBehaviorOptions.AcceptedFiatCurrency.GetDescription()} orders are supported.")
                    .AnswerUser();

            // Calculate total order amount:
            var grpAmount = (double)tokenGrp.Sum(tGrp => tGrp.Amount);
            var tipAmount = 0.0;
            if (participant.Options.Tip > 0)
                tipAmount = ((grpAmount * participant.Options.Tip!.Value) / 100.0);
            var orderFiatAmount = (grpAmount + tipAmount);

            // Check if this order would exceed the session's remaining budget:
            if (orderFiatAmount > session.RemainingBudget)
                throw new InvalidOperationException($"Order rejected!\n" +
                    $"Your order of {orderFiatAmount.Format()} would exceed the session's remaining budget.\n\n" +
                    $"Remaining session budget: {session.RemainingBudget.Format()}")
                    .AddLogLevel(LogLevel.Warning)
                    .AnswerUser();

            // Store the order:
            var order = new OrderRecord
            {
                Tokens = tokenGrp.ToArray(),
                FiatAmount = orderFiatAmount,
                TipAmount = tipAmount,
                Timestamp = DateTimeOffset.Now
            };
            participant.Orders.Add(order);

            // Update log
            await liquidityLogService.LogAsync(cancellationToken).ConfigureAwait(false);
            
            logger.LogInformation("Order registered for user {User} in session {Session}: {OrderAmount}.", user, session, orderFiatAmount.Format(grpCurrency));
        }

        // Confirm order and update status messages:
        if (await botClient.DeleteMessageAsync(participant.UserId, participant.OrderConfirmationMessageId, cancellationToken).ConfigureAwait(false))
            participant.OrderConfirmationMessageId = null;
        var confirmMsg = await botClient.SendMessage(participant.UserId,
            "✅ *Order registered!*\n\n" +
            "_Lightning invoices will be sent to everyone once the session host closes the order phase._",
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        participant.OrderConfirmationMessageId = confirmMsg.MessageId;

        await CloseEditPickerAsync(botClient, participant, cancellationToken).ConfigureAwait(false);

        await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
    }
    private async Task ProcessWinnerInvoiceAsync(ITelegramBotClient botClient, SessionState session, User user, string bolt11, CancellationToken cancellationToken)
    {
        var participant = session.Participants[user.Id];

        // Check if this user is a winner waiting to submit an invoice
        if (!session.WinnerPayouts.TryGetValue(participant, out var winnerPayout))
            throw new InvalidOperationException($"Nice try! Sorry, *you are not a winner* in the current session 😉")
                .AddLogLevel(LogLevel.Information)
                .AnswerUser();
        if (winnerPayout.PaymentCompleted)
            throw new InvalidOperationException($"You have *already submitted your invoice* for this session!")
                .AddLogLevel(LogLevel.Information)
                .AnswerUser();

        logger.LogInformation("Winner invoice submitted by {Participant} for session {Session}.", participant, session);

        // Obtain block header at session end
        var currentBlock = await indexerBackend.GetCurrentBlockAsync(cancellationToken).ConfigureAwait(false);

        // Decode and validate the invoice amount
        var invoiceSats = lightningBackend.GetInvoiceAmount(bolt11);
        ValidateInvoiceAmount(winnerPayout.RemainingAmount, invoiceSats);

        var statusText = "✅ Invoice received!\n" +
            "⏳ Processing payout...";
        var statusMessage = await botClient.SendMessage(participant.UserId, statusText, cancellationToken: cancellationToken).ConfigureAwait(false);

        try
        {
            var paymentResult = await lightningBackend.PayInvoiceAsync(bolt11!, cancellationToken).ConfigureAwait(false);
            if (paymentResult is null)
                throw new InvalidOperationException("Failed to execute payout. Please retry with sending a new invoice.");

            logger.LogInformation("Payout executed successfully by {Participant} for session {Session}.", participant, session);
            winnerPayout.AddPayment(invoiceSats);

            if (winnerPayout.PaymentCompleted)
            {
                if (session.PayoutCompleted)
                {
                    session.Phase = SessionPhase.Completed;
                    session.CompletedAtBlock = currentBlock;

                    // Update the pinned winner message
                    await WinnerMessage.UpdateAsync(session, PaymentStatus.Paid, paymentResult, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
                    
                    // Notify winner about successful payout
                    statusText += "\n\n" +
                        $"✅ Successfully paid out *{invoiceSats.Format()}*.\n" +
                        "🎉 *Session completed!*";
                    await botClient.EditMessageText(user.Id,
                        statusMessage.MessageId,
                        statusText,
                        parseMode: ParseMode.Markdown,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                
                // Update the pinned status message
                await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
                // Update user status messages for all participants
                await UpdateAllParticipantStatusesAsync(session, botClient, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Update lost sats for crash recovery
                await recoveryService.WriteLostSatsAsync(participant, winnerPayout.RemainingAmount, $"Partial payout in session *{session.ChatTitle}*").ConfigureAwait(false);

                // Notify winner about partial payout
                statusText += "\n\n" +
                    $"✅ Partially paid out *{invoiceSats.Format()}*.\n" +
                    $"⚡ Please send me a new invoice for the remaining amount of *{winnerPayout.RemainingAmount.Format()}*.";
                await botClient.EditMessageText(user.Id,
                    statusMessage.MessageId,
                    statusText,
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (session.Phase.IsClosed())
            {
                // Cancel outstanding unpaid invoices:
                await CancelPendingInvoicesAsync(botClient, session, cancellationToken).ConfigureAwait(false);

                // Clean up session
                workflowService.TryCloseSession(session, false);

                await statisticService.OnSessionCompleteAsync(session).ConfigureAwait(false);
                Debug.Assert(session.Statistics is not null);

                // Send statistics
                await GroupStatisticsMessage.SendIfAsync(botClient, statisticService, session, cancellationToken).ConfigureAwait(false);
                foreach (var p in session.Participants.Values)
                    await UserStatisticsMessage.SendIfAsync(botClient, statisticService, p, cancellationToken).ConfigureAwait(false);
            }
            
            await liquidityLogService.LogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Error during payout. Please retry or contact support.", ex);
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
        void appendLimitInfo<T>(string name, T actual, object? available, object? max, string? noLimit, PaymentCurrency currency)
            where T : INumber<T>
        {
            if (max is null)
            {
                if (noLimit is null)
                    ; // Hide
                else
                    diag.AppendLine($"• {name}: *{noLimit}*");
            }
            else
            {
                diag.AppendLine($"• {name}");
                diag.AppendLine($"  • Actual: *{actual.Format(currency, false)}* / *{(max as INumber<T>)!.Format(currency)}*");
                if (T.Zero.CompareTo(actual) < 0)
                    diag.AppendLine($"  • Remaining: *{(available as INumber<T>)!.Format(currency)}*");
            }
        };

        diag.AppendLine("\n🤖 *Bot status:*");
        var maxSessions = "";
        if (botBehaviour.MaxParallelSessions > 0)
            maxSessions = $" of *{botBehaviour.MaxParallelSessions.Value}*";
        diag.AppendLine($"• Active sessions: *{sessionManager.ActiveSessions.Count()}*{maxSessions}");

        appendLimitInfo("Budget", sessionManager.ConsumedServerBudget, sessionManager.AvailableServerBudget, botBehaviour.MaxBudget, "unlimited", BotBehaviorOptions.AcceptedFiatCurrency);
        appendLimitInfo("Sats locked", sessionManager.TotalLockedSats, sessionManager.AvailableLockedSats, botBehaviour.MaxLockedSats, "💤 none", PaymentCurrency.Sats);

        var events = statisticService.GeneralStats?.Events
            .Where(e => e.Value > 0)
            .OrderBy(e => e.Key)
            .ToArray();
        if (events?.IsEmpty() == false)
        {
            diag.AppendLine($"• Events");
            events.ForEach(e => diag.AppendLine($"  • {e.Key.GetDescription()}: *{e.Value}*"));
        }

        // Lost and Found Recovery Information
        diag.AppendLine("\n🔍 *Lost and Found sats recovery:*");
        diag.AppendLine($"• Enabled: *{recoveryService.Enabled}*");
        diag.AppendLine($"• Daily scan time: *{recoveryService.DailyScanTime}*");
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
                    string? idle = (succeeded is null) && (failed is null) ? "💤 " : null;
                    diag.AppendLine($"   • {succeeded}{failed}{idle}{host.Hostname}");
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
        diag.AppendLine($"• Received blocks: *{indexerBackend.ReceivedBlocks}*");
        diag.AppendLineIfNotNull("• Latest block: {0}", indexerBackend.LastBlock?.Format());

        // Exchange rate backend Information (optional)
        diag.AppendLine("\n💱 *Exchange rate backend status:* ");
        appendBackendInfo(exchangeRateBackend);
        diag.AppendLineIfNotNull("• Last update: *{0}*", exchangeRateBackend.LastRateUpdate?.ToString("f"), "⚠️ never");
        if (exchangeRateBackend.RatesReliable)
            diag.AppendLine($"• Fiat rate: *{exchangeRateBackend.FiatRate!.Value.FormatFiatRate()}*");

        // Lightning backend Information
        diag.AppendLine("\n⚡ *Lightning backend status:*");
        appendBackendInfo(lightningBackend);

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
            throw new InvalidOperationException("The recover command is only available in private chat with the bot.\n" +
                "Please send me `/recover` as a direct message.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser();

        // Get lost sats for this user
        var lostSats = await recoveryService.TryGetLostSatsAsync(command.UserId).ConfigureAwait(false);
        if (lostSats is null)
            throw new Exception("You *don't have any lost sats* to recover.\n" +
                "All your previous payments were successfully processed.")
                .AddLogLevel(LogLevel.Information)
                .AnswerUser();

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
            "✅ Recovery invoice received!\n" +
            "⏳ Processing recovery payout...", 
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Attempt to pay the invoice
        var paymentResult = await lightningBackend.PayInvoiceAsync(bolt11, cancellationToken).ConfigureAwait(false);
        if (paymentResult is not null)
        {
            if (invoiceSats == expectedSats)
            {
                // Clear lost sats record
                await recoveryService.ClearLostSatsAsync(user.Id).ConfigureAwait(false);
                
                await botClient.SendMessage(user.Id,
                    $"🎉 *Recovery completed!*\n\n" +
                    $"✅ Successfully claimed *{expectedSats.Format()}*\n" +
                    $"Your lost sats have been sent to your Lightning wallet! 🚀",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                logger.LogInformation("Successfully processed recovery payout for user {User}: {SatsAmount} from {Timestamp}.", user, expectedSats.Format(), lostSats.Timestamp);
            }
            else
            {
                // Update lost sats for remaining amount
                lostSats.SatsAmount = (expectedSats - invoiceSats);
                lostSats.Reason = "Partial recovery payout";
                await recoveryService.WriteLostSatsAsync(lostSats).ConfigureAwait(false);
                
                // Notify user about partial payout
                await botClient.SendMessage(user.Id,
                    $"✅ Partially recovered *{invoiceSats.Format()}*.\n" +
                    $"⚡ Please send me a new invoice to claim the remaining amount of *{lostSats.SatsAmount.Format()}*.",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                
                logger.LogInformation("Successfully processed partial recovery payout for user {User}: {SatsAmount} from {Timestamp}.", user, invoiceSats.Format(), lostSats.Timestamp);
            }
        }
        else
        {
            logger.LogError("Failed to process recovery payout for user {User}: {SatsAmount}.", user, expectedSats.Format());
                
            throw new InvalidOperationException("Recovery payout failed!\n\n" +
                "Unable to process your recovery invoice. Please try again later or contact support.")
                .AddLogLevel(LogLevel.Error)
                .AnswerUser();
        }
    }


    #region Helper
    private async Task UpdateAllParticipantStatusesAsync(SessionState session, ITelegramBotClient botClient, CancellationToken cancellationToken)
    {
        foreach (var participant in session.Participants.Values)
        {
            await CloseEditPickerAsync(botClient, participant, cancellationToken).ConfigureAwait(false);
            await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        }
    }
    private async Task CloseEditPickerAsync(ITelegramBotClient botClient, ParticipantState participant, CancellationToken cancellationToken)
    {
        if (await botClient.DeleteMessageAsync(participant.UserId, participant.EditPickerMessageId, cancellationToken).ConfigureAwait(false))
            participant.EditPickerMessageId = null;
    }
    private void ValidateInvoiceAmount(long expectedSats, long invoiceSats)
    {
        if (invoiceSats > expectedSats)
            throw new InvalidOperationException($"No way! That's too much 😄\n\n" +
                $"Your invoice: {invoiceSats.Format()}\n" +
                $"Expected amount: {expectedSats.Format()}\n" +
                "ℹ️ Please create a new invoice with the correct amount of sats.")
                .AnswerUser()
                .AddLogLevel(LogLevel.Warning);
    }
    private async Task<bool> IsRootUserAsync(ITelegramBotClient botClient, CommandMessage command, CancellationToken cancellationToken)
    {
        // Check if user is a root user
        if (!telegramSettings.RootUsers.Contains(command.UserId))
            return (false);

        // Check if command is used in private chat
        var chat = await botClient.GetChat(command.ChatId, cancellationToken).ConfigureAwait(false);
        if (chat.Type != ChatType.Private)
            throw new InvalidOperationException("This command is only available in private chat with the bot!")
                .AnswerUser();

        return (true);
    }
    #endregion
}

