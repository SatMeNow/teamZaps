using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using teamZaps.Configuration;
using teamZaps.Services;
using teamZaps.Utils;
using Telegram.Bot.Types.ReplyMarkups;

namespace teamZaps.Sessions;


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
            replyMarkup: BuildKeyboard(session, 0),
            cancellationToken: cancellationToken);

        session.StatusMessageId = statusMessage.MessageId;

        await botClient.PinChatMessage(session.ChatId, statusMessage.MessageId, cancellationToken: cancellationToken);

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
                replyMarkup: BuildKeyboard(session, 0),
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && 
            (ex.Message.Contains("message to edit not found") || ex.Message.Contains("message can't be edited")))
        {
            // Message was deleted, recreate it
            logger.LogInformation("Status message deleted for session {Session}, recreating...", session);
            await RecreateAsync(session, botClient, workflowService, logger, cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
            // Message content is the same, this is fine
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update pinned status message for session {Session}", session);
        }
        
        if ((session.Phase.IsClosed()) && (session.StatusMessageId is not null))
        {
            // Unpin status message
            await botClient.UnpinChatMessage(
                chatId: session.ChatId,
                messageId: session.StatusMessageId.Value,
                cancellationToken: cancellationToken);
        }
    }
    private static async Task RecreateAsync<TLogger>(SessionState session, ITelegramBotClient botClient, SessionWorkflowService workflowService, ILogger<TLogger> logger, CancellationToken cancellationToken)
    {
        try
        {
            var message = await SendAsync(session, botClient, workflowService, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recreate status message for session {Session}", session);
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

        if (session.HasPayments)
        {
            status.AppendLine($"• Payments: *{session.Payments.Count()}*");
            status.AppendLine($"• Total: 💶 {session.FormatTotalFiatAmount()}");
        }

        if (!session.Participants.IsEmpty)
        {
            status.AppendLine($"\n*{session.Participants.Count}* Participant(s):");
            foreach (var participant in session.Participants)
            {
                var joinedLottery = "";
                if (session.LotteryParticipants.ContainsKey(participant.Key))
                    joinedLottery = "🎫 ";
                var p = $"• {joinedLottery}{participant.Value.MarkdownDisplayName()}";
                if (participant.Value.HasPayments)
                    p += $": {participant.Value.FormatAmount()}";
                status.AppendLine(p);
            }
        }
        
        if (session.Phase == SessionPhase.WaitingForLotteryParticipants)
        {
            status.AppendLine($"\n🎯 *New session* started by {session.StartedByUser.MarkdownDisplayName()}!");
            status.AppendLine("\n⚠️ *Payments are blocked* until someone enters the lottery first!");
        }

        if (session.Winners.Count == 1)
        {
            var winner = session.WinnerUser!;
            status.AppendLine($"\n🏆 Winner: {winner.MarkdownDisplayName()} ({session.Winners[winner.UserId].FiatAmount.Format()})");
        }
        else if (session.Winners.Count > 1)
        {
            status.AppendLine($"\n🏆 {session.Winners.Count} winners:");
            foreach (var winnerEntry in session.Winners)
            {
                var winner = session.Participants[winnerEntry.Key];
                var invoiceState = (winner.SubmittedInvoice ? "✅" : "⏳");
                status.AppendLine($"• {invoiceState} {winner.MarkdownDisplayName()} ({winnerEntry.Value.FiatAmount.Format()})");
            }
        }

        return (status.ToString());
    }

    private static InlineKeyboardMarkup? BuildKeyboard(SessionState session, long userId)
    {
        if (session.Phase <= SessionPhase.AcceptingPayments)
        {
            bool alreadyJoined = session.Participants.ContainsKey(userId);
            var joinButton = InlineKeyboardButton.WithCallbackData(alreadyJoined ? "✅ Joined" : "🎯 Join", CallbackActions.JoinSession);
            InlineKeyboardButton closeButton;
            if (session.HasPayments)
                closeButton = InlineKeyboardButton.WithCallbackData("🏆 Close", CallbackActions.CloseSession);
            else
                closeButton = InlineKeyboardButton.WithCallbackData("❌ Cancel", CallbackActions.CancelSession);
            return new InlineKeyboardMarkup(new[] { joinButton, closeButton });
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
                replyMarkup: BuildKeyboard(session),
                cancellationToken: cancellationToken);

            await botClient.PinChatMessage(message.Chat.Id, message.MessageId, cancellationToken: cancellationToken);
                
            // Store message ID
            participant.StatusMessageId = message.MessageId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send private status message to user {User}", participant);
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
            var chat = await botClient.GetChat(session.ChatId, cancellationToken);
            await botClient.EditMessageText(
                chatId: participant.UserId,
                messageId: messageId,
                text: Build(session, participant),
                parseMode: ParseMode.Markdown,
                replyMarkup: BuildKeyboard(session, participant),
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && 
            (ex.Message.Contains("message to edit not found") || ex.Message.Contains("message can't be edited")))
        {
            // Message was deleted, recreate it
            logger.LogInformation("User status message deleted for user {User}, recreating...", participant);
            await RecreateAsync(session, participant, botClient, workflowService, logger, cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
        {
            // Message content is the same, this is fine
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update user status message for user {User}", participant);
        }
    }
    private static async Task RecreateAsync(SessionState session, ParticipantState participant, ITelegramBotClient botClient, SessionWorkflowService workflowService, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(session, participant, botClient, workflowService, logger, cancellationToken);
            logger.LogInformation("User status message recreated for user {User}", participant);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recreate user status message for user {User}", participant);
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
            if (session.LotteryParticipants.TryGetValue(participant.UserId, out var budget))
                status.AppendLine($"• Lottery: 🎫 *Joined* (budget: {budget.Format()})");
            else
                status.AppendLine("• Lottery: 🎟️ *Not joined*");
            
            if (session.Phase >= SessionPhase.AcceptingPayments)
            {
                var tip = participant.Tip.FormatTip();
                if (participant.Tip > 0)
                    tip = $"🎩 *{tip}* per payment";
                status.AppendLine($"• Tip: {tip}");
            }
        }
        status.AppendLine();

        if ((session.Phase >= SessionPhase.AcceptingPayments) && (participant?.HasPayments == true))
        {
            status.AppendLine("*Payments:*");
            status.AppendPayments(participant.Payments);
            status.AppendLine();
            
            status.AppendLine($"💶 Total: {participant.FormatTotalFiatAmount()}");
            status.AppendLine();
        }
        
        switch (session.Phase)
        {
            case SessionPhase.WaitingForLotteryParticipants:
                status.AppendLine("🎰 *Enter the lottery* if you're willing to pay fiat! Set your maximum budget.\n");
                status.AppendLine("⚠️ *Payments are blocked* until someone enters the lottery first!");
                break;
            case SessionPhase.AcceptingPayments:
                status.AppendLine("Use the button below to *make payments*.");
                status.AppendLine("I'll create Lightning invoices for you to pay.");
                break;
        }
        
        return status.ToString();
    }


    private static InlineKeyboardMarkup? BuildKeyboard(SessionState session, ParticipantState? participant = null)
    {
        var buttons = new List<InlineKeyboardButton>();
        if ((session.Phase <= SessionPhase.AcceptingPayments) && (participant?.JoinedLottery(session) != true))
            buttons.Add(InlineKeyboardButton.WithCallbackData("🎰 Enter Lottery", CallbackActions.JoinLottery));
        if (session.Phase == SessionPhase.AcceptingPayments)
        {
            buttons.Add(InlineKeyboardButton.WithCallbackData("🎩 Set tip", CallbackActions.SetTip));
            buttons.Add(InlineKeyboardButton.WithCallbackData("💰 Make Payment", CallbackActions.MakePayment));
        }
        return (new InlineKeyboardMarkup(buttons));
    }
}