using System.Text;
using TeamZaps.Services;
using TeamZaps.Session;
using TeamZaps.Utils;
using Telegram.Bot.Types.ReplyMarkups;

namespace TeamZaps.Handlers;


/// <summary>
/// Pinned session status message in group-chat.
/// </summary>
internal static class SessionStatusMessage
{
    public static async Task<Message> SendAsync(SessionState session, ITelegramBotClient botClient, SessionWorkflowService workflowService, CancellationToken cancellationToken)
    {
        var statusMessage = await botClient.SendMessage(session.ChatId,
            text: Build(session),
            parseMode: ParseMode.Markdown,
            linkPreviewOptions: true,
            replyMarkup: BuildKeyboard(session, 0),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        session.StatusMessageId = statusMessage.MessageId;

        if (session.BotCanPinMessages)
            await botClient.PinChatMessage(session.ChatId, statusMessage.MessageId, cancellationToken: cancellationToken).ConfigureAwait(false);

        return (statusMessage);
    }
    public static async Task UpdateAsync<TLogger>(SessionState session, ITelegramBotClient botClient, SessionWorkflowService workflowService, ILogger<TLogger> logger, CancellationToken cancellationToken)
    {
        if (session.StatusMessageId is null)
            return;

        try
        {
            await botClient.EditMessageText(
                chatId: session.ChatId,
                messageId: session.StatusMessageId.Value,
                text: Build(session),
                parseMode: ParseMode.Markdown,
                linkPreviewOptions: true,
                replyMarkup: BuildKeyboard(session, 0),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && 
            (ex.Message.Contains("message to edit not found") || ex.Message.Contains("message can't be edited")))
        {
            // Message was deleted, recreate it
            logger.LogInformation("Status message deleted for session {Session}, recreating...", session);
            await RecreateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
            // Message content is the same, this is fine
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update pinned status message for session {Session}.", session);
        }
        
        // Unpin status message
        if ((session.BotCanPinMessages) && (session.Phase.IsClosed()) && (session.StatusMessageId is not null))
            await botClient.UnpinChatMessage(
                chatId: session.ChatId,
                messageId: session.StatusMessageId.Value,
                cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    private static async Task RecreateAsync<TLogger>(SessionState session, ITelegramBotClient botClient, SessionWorkflowService workflowService, ILogger<TLogger> logger, CancellationToken cancellationToken)
    {
        try
        {
            var message = await SendAsync(session, botClient, workflowService, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recreate status message for session {Session}.", session);
            session.StatusMessageId = null; // Clear invalid message ID
        }
    }
    private static string Build(SessionState session)
    {
        var status = new StringBuilder();
        
        status.AppendSessionState(session);

        // Show lottery entries with budgets
        if (session.LotteryParticipants.Count > 0)
        {
            status.AppendLine($"• Lottery entries: 🎫 *{session.LotteryParticipants.Count}*");
            status.AppendLine($"• Total budget: 💰 *{session.Budget.Format()}*");
        }
        if (session.HasOrders)
        {
            status.AppendLine($"• Orders: *{session.Orders.Count()}*");
            status.AppendLine($"• Total: 💶 {session.FormatOrderedAmount()}");
        }

        if (!session.Participants.IsEmpty)
        {
            status.AppendLine($"\n*{session.Participants.Count}* Participant(s):");
            foreach (var participant in session.Participants.Values)
            {
                var icons = "";
                if ((participant.HasOrders) && (session.Phase == SessionPhase.WaitingForPayments))
                    icons += (participant.HasPayments) ? " ✅" : "🫰";
                if (session.LotteryParticipants.ContainsKey(participant))
                    icons += "🎫";
                if (icons.Length > 0)
                    icons += " ";
                var p = $"• {icons}{participant.MarkdownDisplayName()}";
                if (participant.HasOrders && !participant.HasPayments)
                    p += $": {participant.OrdersFiatAmount.Format()}";
                else if (participant.HasPayments)
                    p += $": {participant.FormatAmount()}";
                status.AppendLine(p);
            }
        }
        
        if (session.Phase == SessionPhase.WaitingForLotteryParticipants)
        {
            status.AppendLine($"\n🎯 *New session* started by {session.StartedByUser.MarkdownDisplayName()}!");
            status.AppendLine("\n⚠️ *Orders are blocked* until someone enters the lottery first!");
        }

        if (session.WinnerPayouts.Count == 1)
        {
            var winner = session.Winner!;
            status.AppendLine($"\n🏆 Winner: {winner.MarkdownDisplayName()} ({session.WinnerPayouts[winner].FiatAmount.Format()})");
        }
        else if (session.WinnerPayouts.Count > 1)
        {
            status.AppendLine($"\n🏆 {session.WinnerPayouts.Count} winners:");
            foreach (var winnerPayout in session.WinnerPayouts)
            {
                var winner = session.Participants[winnerPayout.Key];
                var invoiceState = (winnerPayout.Value.PaymentCompleted ? "✅" : "⏳");
                status.AppendLine($"• {invoiceState} {winner.MarkdownDisplayName()} ({winnerPayout.Value.FiatAmount.Format()})");
            }
        }

        return (status.ToString());
    }

    private static InlineKeyboardMarkup? BuildKeyboard(SessionState session, long userId)
    {
        if (session.Phase <= SessionPhase.AcceptingOrders)
        {
            bool alreadyJoined = session.Participants.ContainsKey(userId);
            var joinButton = InlineKeyboardButton.WithCallbackData(alreadyJoined ? "✅ Joined" : "🎯 Join", CallbackActions.JoinSession);
            InlineKeyboardButton closeButton;
            if (session.HasOrders)
                closeButton = InlineKeyboardButton.WithCallbackData("🏆 Close", CallbackActions.CloseSession);
            else
                closeButton = InlineKeyboardButton.WithCallbackData("❌ Cancel", CallbackActions.CancelSession);
            return new InlineKeyboardMarkup(new[] { joinButton, closeButton });
        }
        else if (session.Phase == SessionPhase.WaitingForPayments)
        {
            var forceCloseButton = InlineKeyboardButton.WithCallbackData("🏆‼️ Force close", CallbackActions.ForceClose);
            return new InlineKeyboardMarkup(new[] { forceCloseButton });
        }
        else
            return (null);
    }
}

/// <summary>
/// Pinned status message in a user's bot-chat.
/// Also used as welcome message.
/// </summary>
internal static class UserStatusMessage
{
    public static async Task SendAsync(SessionState session, ParticipantState participant, ITelegramBotClient botClient, SessionWorkflowService workflowService, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var message = await botClient.SendMessage(participant.UserId,
                text: Build(session),
                parseMode: ParseMode.Markdown,
                linkPreviewOptions: true,
                replyMarkup: BuildKeyboard(session),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await botClient.PinChatMessage(message.Chat.Id, message.MessageId, cancellationToken: cancellationToken).ConfigureAwait(false);
                
            // Store message ID
            participant.StatusMessageId = message.MessageId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send private status message to user {User}.", participant);
            throw;
        }
    }
    public static async Task UpdateAsync(SessionState session, ParticipantState participant, ITelegramBotClient botClient, SessionWorkflowService workflowService, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
    {
        if (participant.StatusMessageId is null)
            return;
        
        var messageId = participant.StatusMessageId.Value;

        try
        {
            var chat = await botClient.GetChat(session.ChatId, cancellationToken).ConfigureAwait(false);
            await botClient.EditMessageText(
                chatId: participant.UserId,
                messageId: messageId,
                text: Build(session, participant),
                parseMode: ParseMode.Markdown,
                linkPreviewOptions: true,
                replyMarkup: BuildKeyboard(session, participant),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && 
            (ex.Message.Contains("message to edit not found") || ex.Message.Contains("message can't be edited")))
        {
            // Message was deleted, recreate it
            logger.LogInformation("User status message deleted for user {User}, recreating...", participant);
            await RecreateAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
            // Message content is the same, this is fine
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update user status message for user {User}.", participant);
        }
    }
    private static async Task RecreateAsync(SessionState session, ParticipantState participant, ITelegramBotClient botClient, SessionWorkflowService workflowService, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("User status message recreated for user {User}.", participant);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recreate user status message for user {User}.", participant);
            participant.StatusMessageId = null;
        }
    }
    private static string Build(SessionState session, ParticipantState? participant = null)
    {
        var status = new StringBuilder();
        status.AppendLine($"🎉 Welcome to the *{session.ChatTitle}* Team Zaps session!\n");

        status.AppendSessionState(session);
        if (participant is not null)
        {
            if (session.LotteryParticipants.TryGetValue(participant, out var budget))
                status.AppendLine($"• Lottery: 🎫 *Joined* (budget: {budget.Format()})");
            else
                status.AppendLine("• Lottery: 🎟️ *Not joined*");
            
            if (session.Phase >= SessionPhase.AcceptingOrders)
            {
                var tip = participant.Options.Tip.FormatTip();
                if (participant.Options.Tip > 0)
                    tip = $"🎩 *{tip}* per order";
                status.AppendLine($"• Tip: {tip}");
            }
        }
        status.AppendLine();

        if ((session.Phase >= SessionPhase.AcceptingOrders) && (participant?.HasOrders == true))
        {
            status.AppendLine("*Orders:*");
            status.AppendOrders(participant.Orders);
            status.AppendLine();
            status.AppendLine($"💶 Total: {participant.FormatOrderedAmount()}");
            status.AppendLine();
        }

        switch (session.Phase)
        {
            case SessionPhase.WaitingForLotteryParticipants:
                status.AppendLine("🎰 *Enter the lottery* if you're willing to pay fiat! Set your maximum budget.\n");
                status.AppendLine("⚠️ *Orders are blocked* until someone enters the lottery first!");
                break;
            case SessionPhase.AcceptingOrders:
                status.AppendLine("📋 Use the button below to *add your orders*.");
                break;
            case SessionPhase.WaitingForPayments:
                status.AppendLine("⚡ *Invoice sent!* Please pay your Lightning invoice to participate.");
                break;
        }
        
        return status.ToString();
    }


    private static InlineKeyboardMarkup? BuildKeyboard(SessionState session, ParticipantState? participant = null)
    {
        var buttons = new List<InlineKeyboardButton>();
        if ((session.Phase <= SessionPhase.AcceptingOrders) && (participant?.JoinedLottery(session) != true))
            buttons.Add(InlineKeyboardButton.WithCallbackData("🎰 Enter Lottery", CallbackActions.JoinLottery));
        if (session.Phase == SessionPhase.AcceptingOrders)
        {
            buttons.Add(InlineKeyboardButton.WithCallbackData("🎩 Set tip", CallbackActions.SetTip));
            buttons.Add(InlineKeyboardButton.WithCallbackData("📋 Add Order", CallbackActions.AddOrder));
            if (participant?.HasOrders == true)
                buttons.Add(InlineKeyboardButton.WithCallbackData("✏️ Edit Order", CallbackActions.ShowEditPicker));
        }
        return (new InlineKeyboardMarkup(buttons));
    }
}

/// <summary>
/// Item-picker message sent to the user's DM during AcceptingOrders phase.
/// Shows a row per PaymentToken with ✏️ (edit) and 🗑️ (remove) buttons.
/// </summary>
internal static class EditOrderPickerMessage
{
    public static async Task SendAsync(ParticipantState participant, ITelegramBotClient botClient, long chatId, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
    {
        var keyboard = BuildKeyboard(participant);
        if (keyboard is null)
            return;

        try
        {
            var message = await botClient.SendMessage(chatId,
                BuildText(),
                parseMode: ParseMode.Markdown,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            participant.EditPickerMessageId = message.MessageId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send edit picker to user {User}.", participant);
        }
    }

    public static async Task UpdateAsync(ParticipantState participant, ITelegramBotClient botClient, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
    {
        if (participant.EditPickerMessageId is null)
            return;

        var keyboard = BuildKeyboard(participant);
        if (keyboard is null)
            return;

        try
        {
            await botClient.EditMessageReplyMarkup(
                chatId: participant.UserId,
                messageId: participant.EditPickerMessageId.Value,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
            // fine
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update edit picker for user {User}.", participant);
        }
    }

    private static string BuildText() => "📋 *Edit your orders*\n\nTap ✏️ to replace an item or 🗑️ to remove it:";

    public static InlineKeyboardMarkup? BuildKeyboard(ParticipantState participant)
    {
        var allTokens = participant.Orders
            .SelectMany((order, oi) => order.Tokens.Select((token, ti) => (OrderIndex: oi, TokenIndex: ti, Token: token)))
            .ToList();

        if (allTokens.Count == 0)
            return null;

        var rows = allTokens
            .Select(t => (IEnumerable<InlineKeyboardButton>)new[]
            {
                InlineKeyboardButton.WithCallbackData($"✏️ {t.Token}", $"{CallbackActions.EditToken}_{t.OrderIndex}_{t.TokenIndex}"),
                InlineKeyboardButton.WithCallbackData("🗑️", $"{CallbackActions.RemoveToken}_{t.OrderIndex}_{t.TokenIndex}")
            })
            .Append(new[] { InlineKeyboardButton.WithCallbackData("✖️ Close", CallbackActions.CancelEdit) });

        return new InlineKeyboardMarkup(rows);
    }
}