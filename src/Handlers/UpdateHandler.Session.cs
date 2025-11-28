using System.Text;
using teamZaps.Services;
using teamZaps.Sessions;
using teamZaps.Utils;

namespace teamZaps.Handlers;

public partial class UpdateHandler
{
    private async Task HandleStartSessionAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var userId = message.From!.Id;
        var displayName = message.From.GetDisplayName();

        // Check if request was done for a group chat:
        var chat = await botClient.GetChat(chatId);
        if (chat.Type != ChatType.Group && chat.Type != ChatType.Supergroup)
        {
            await botClient.SendMessage(chatId, 
                "❌ Sessions can only be started in group chats.",
                cancellationToken: cancellationToken);
            return;
        }

        // Check if only admins can start sessions
        if (!workflowService.Options.AllowNonAdminSessionStart)
        {
            if (!await IsUserAdminAsync(botClient, chatId, userId, cancellationToken))
            {
                await botClient.SendMessage(chatId, 
                    "❌ Only group administrators can start a session.", 
                    cancellationToken: cancellationToken);
                return;
            }
        }

        if (workflowService.TryStartSession(chat, userId, displayName, out var session))
        {
            await SessionStatusMessage.SendAsync(session, botClient, workflowService, cancellationToken);
            logger.LogInformation("Session started in chat {ChatId} by user {UserId}", chatId, userId);
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
        if (!workflowService.Options.AllowNonAdminSessionClose)
        {
            if (!await IsUserAdminAsync(botClient, chatId, userId, cancellationToken))
            {
                await botClient.SendMessage(chatId,
                    "❌ Only group administrators can close a session.",
                    cancellationToken: cancellationToken);
                return;
            }
        }

        var session = workflowService.GetSessionByChat(chatId);
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

            workflowService.TryCloseSession(chatId);
        }
        else
        {
            // Draw winner immediately
            var participants = session.LotteryParticipants.ToArray();
            var winnerUserId = participants[Random.Shared.Next(participants.Length)];
            
            session.WinnerUserId = winnerUserId;
            session.Phase = SessionPhase.WaitingForInvoice;

            await SessionSummaryMessage.SendAsync(session, botClient, logger, cancellationToken);
            
            await WinnerMessage.SendAsync(session, botClient, workflowService, cancellationToken);

            logger.LogInformation("Winner selected immediately for chat {ChatId}: user {UserId}", chatId, winnerUserId);
        }
        
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);
        
        // Update user status messages for all participants
        foreach (var participantId in session.Participants.Keys)
        {
            await UserStatusMessage.UpdateAsync(session, participantId, botClient, workflowService, logger, cancellationToken);
        }
    }
    private async Task HandleCancelSessionAsync(ITelegramBotClient botClient, long chatId, long userId, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByChat(chatId);
        if (session is null)
            return;

        // Check permissions
        if (session?.HasPayments == false)
            ; // Skip (No need to check)
        else if (!workflowService.Options.AllowNonAdminSessionCancel)
        {
            if (!await IsUserAdminAsync(botClient, chatId, userId, cancellationToken))
            {
                await botClient.SendMessage(chatId,
                    "❌ Only group administrators can cancel a session.",
                    cancellationToken: cancellationToken);
                return;
            }
        }

        if (workflowService.TryCloseSession(chatId))
        {
            await SessionStatusMessage.UpdateAsync(session!, botClient, workflowService, logger, cancellationToken);
            
            // Update user status messages for all participants
            foreach (var participantId in session!.Participants.Keys)
            {
                await UserStatusMessage.UpdateAsync(session, participantId, botClient, workflowService, logger, cancellationToken);
            }
        
            await botClient.SendMessage(chatId,
                "❌ Session has been cancelled and removed.",
                cancellationToken: cancellationToken);
            logger.LogInformation("Session cancelled in chat {ChatId} by user {UserId}", chatId, userId);
        }
        else
        {
            await botClient.SendMessage(chatId,
                "⚠️ No active session to close.",
                cancellationToken: cancellationToken);
        }   
    }
    private async Task HandleJoinSessionAsync(ITelegramBotClient botClient, long chatId, long userId, string displayName, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByChat(chatId);
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

        // Send private status message
        try
        {
            await UserStatusMessage.SendAsync(session, userId, displayName, botClient, workflowService, logger, cancellationToken);
        }
        catch (Exception)
        {
            var warningMessage = await botClient.SendMessage(chatId,
                $"⚠️ {displayName}, please start a private chat with me first by clicking @{(await botClient.GetMe(cancellationToken)).Username}",
                cancellationToken: cancellationToken);
                
            // Mark user as pending session join with message ID for later deletion
            session.PendingJoins[userId] = (chatId, warningMessage.MessageId);

            return;
        }

        // Update the pinned status message
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);

        logger.LogInformation("User {UserId} joined session in chat {ChatId}", userId, chatId);
    }
    private async Task HandleJoinLotteryAsync(ITelegramBotClient botClient, long chatId, long userId, string displayName, int messageId, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByUser(userId);
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
                
                await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);
                
                // Update user status messages for all participants
                foreach (var participant in session.Participants.Values)
                {
                    if (participant.UserId == userId)
                        participant.JoinedLottery = true;

                    await UserStatusMessage.UpdateAsync(session, participant.UserId, botClient, workflowService, logger, cancellationToken);
                }
                
                logger.LogInformation("First lottery participant {UserId} in chat {ChatId}, payments unlocked", userId, chatId);
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
                await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);
                
                logger.LogInformation("User {UserId} joined lottery in chat {ChatId}", userId, chatId);
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
    /// <summary>
    /// Views the current session status.
    /// </summary>
    /// <remarks>
    /// Maybe useful if the status message is far above in chat history and you want to move it down.
    /// </remarks>
    private async Task HandleStatusAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        var session = workflowService.GetSessionByChat(chatId);
        if (session is null)
        {
            await botClient.SendMessage(chatId,
                "ℹ️ No active session in this group.\n\nUse /startsession to start one!",
                cancellationToken: cancellationToken);
            return;
        }

        // Delete previous message (should exist, but we don't really know):
        if (session.StatusMessageId is not null)
            await botClient.DeleteMessage(chatId, session.StatusMessageId!.Value);

        await SessionStatusMessage.SendAsync(session, botClient, workflowService, cancellationToken);
    }
}
