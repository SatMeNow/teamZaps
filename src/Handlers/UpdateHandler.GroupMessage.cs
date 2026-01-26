using System.Diagnostics;
using System.Text;
using TeamZaps.Configuration;
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
        
        // Check server-wide budget limit
        var minBudget = botBehaviour.BudgetChoices.Min();
        if (!CheckServerBudgetLimit(minBudget))
            throw new InvalidOperationException("💸 Starting a new session would exceed the server-wide budget limit!\n\n" +
                "Please try again later when some budgets are available.")
                .AddLogLevel(LogLevel.Information)
                .AnswerUser()
                .ExpireMessage();

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

        if (session.Phase > SessionPhase.AcceptingPayments)
            throw new InvalidOperationException("Session has already moved past the payment phase.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();

        if (session.HasPayments)
        {
            // Draw winners based on budget limits
            SelectWinners(session);
            session.Phase = SessionPhase.WaitingForInvoice;

            var winners = string.Join(", ", session.WinnerPayouts.Select(w => $"{w.Key} ({w.Value.FiatAmount.Format()})"));
            logger.LogInformation("Winner(s) selected for session {Session}: {Winners}.", session, winners);

            // Update recovery files(remove losers, add winner payouts):
            var losers = session.Participants.Values.Except(session.Winners);
            recoveryService.ClearLostSats(losers);
            foreach (var winner in session.WinnerPayouts)
                await recoveryService.WriteLostSatsAsync(winner.Key, winner.Value.SatsAmount, $"Winner payout for session *{session.ChatTitle}*").ConfigureAwait(false);
            
            // Update session status messages
            await SessionSummaryMessage.SendAsync(botClient, logger, session, cancellationToken).ConfigureAwait(false);
            await WinnerMessage.SendAsync(session, botClient, workflowService, cancellationToken).ConfigureAwait(false);
            await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
            
            // Update user status messages for all participants
            foreach (var participant in session.Participants.Values)
                await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
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
            await SessionStatusMessage.UpdateAsync(session!, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
            
            // Update user status messages for all participants
            foreach (var participant in session!.Participants.Values)
                await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        
            await botClient.SendMessage(chatId, "❌ Session has been cancelled and removed.", cancellationToken: cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Session {Session} cancelled by user {User}.", session, user);
        }
        else
            throw new InvalidOperationException("No active session to close.")
                .AddLogLevel(LogLevel.Warning)
                .AnswerUser()
                .ExpireMessage();   
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
        if (session.Phase > SessionPhase.AcceptingPayments)
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
            var botUser = await botClient.GetMe(cancellationToken).ConfigureAwait(false);
            var welcomeMessage = await botClient.SendMessage(chatId,
                $"Hey @{user.Username}, we did not meet before ✌️\n" +
                "I'm a telegram bot, *helping you* and your friends *to coordinate lightning payments*.\n\n" +
                $"ℹ️ Please *start a private chat* to interact with me, by clicking @{botUser.Username}. See you soon 👍",
                parseMode: ParseMode.Markdown,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Mark user as pending session join
            sessionManager.PendingJoins[user.Id] = new PendingJoinInfo(chatId, welcomeMessage.MessageId);
            // Remove user from participants to avoid inconsistencies
            session.Participants.Remove(user.Id, out _);

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
    private int SelectWinners(SessionState session)
    {
        Debug.Assert(session.WinnerPayouts.IsEmpty());

        var remainingAmount = ((ITipableAmount)session).TotalFiatAmount;

        // Shuffle participants for fair random selection
        var seed = HashCode.Combine(Environment.TickCount, session.ChatTitle, session.StartedAtBlock?.Height, session.Participants.Count, remainingAmount);
        var random = new Random(seed);
        var lotteryParticipants = session.LotteryParticipants
            .OrderBy(_ => random.Next())
            .ToArray();

        foreach (var participant in lotteryParticipants)
        {
            if (remainingAmount <= 0)
                break;

            var userId = participant.Key;
            var budget = participant.Value;
            var amountToPay = Math.Min(budget, remainingAmount);
            var satsAmount = CalculateWinnerSats(session, amountToPay);
            
            session.WinnerPayouts[participant.Key] = new PayableFiatAmount(amountToPay, satsAmount);

            remainingAmount -= amountToPay;
        }

        return (session.WinnerPayouts.Count);
    }

    private bool CheckServerBudgetLimit(double requestedBudget)
    {
        if (botBehaviour.MaxBudget is null)
            return (true);
        else
            return ((sessionManager.ConsumedServerBudget + requestedBudget) <= botBehaviour.MaxBudget.Value);
    }
    private static long CalculateWinnerSats(SessionState session, double fiatAmount)
    {
        // Don't use any exchange rate here!
        // > Just calculate proportionally to the total fiat and sats amounts.
        var totalFiat = ((ITipableAmount)session).TotalFiatAmount;
        var totalSats = session.SatsAmount;
        return (long)(totalSats * (fiatAmount / totalFiat));
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
