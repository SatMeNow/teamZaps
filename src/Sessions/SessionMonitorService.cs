using teamZaps.Services;

namespace teamZaps.Sessions;

public class SessionMonitorService : BackgroundService
{
    private readonly SessionManager _sessionManager;
    private readonly SessionWorkflowService _workflow;
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<SessionMonitorService> _logger;

    public SessionMonitorService(
        SessionManager sessionManager,
        SessionWorkflowService workflow,
        ITelegramBotClient botClient,
        ILogger<SessionMonitorService> logger)
    {
        _sessionManager = sessionManager;
        _workflow = workflow;
        _botClient = botClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckSessionsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during session monitoring loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CheckSessionsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var session in _sessionManager.ActiveSessions)
        {
            switch (session.Phase)
            {
                case SessionPhase.LotteryOpen:
                    if (session.LotteryClosesAt.HasValue && now >= session.LotteryClosesAt.Value)
                    {
                        _logger.LogInformation("Lottery closed for chat {ChatId}, selecting winner...", session.ChatId);
                        
                        if (session.LotteryParticipants.Count == 0)
                        {
                            // No one joined the lottery, cancel session
                            await _botClient.SendMessage(
                                chatId: session.ChatId,
                                text: "❌ No one joined the lottery. Session cancelled.",
                                cancellationToken: cancellationToken);
                            _sessionManager.RemoveSession(session.ChatId);
                            break;
                        }

                        // Select random winner
                        var participants = session.LotteryParticipants.ToArray();
                        var random = new Random();
                        var winnerUserId = participants[random.Next(participants.Length)];
                        
                        session.WinnerUserId = winnerUserId;
                        session.Phase = SessionPhase.WaitingForInvoice;

                        var winner = session.Participants[winnerUserId];
                        var totalSats = _workflow.TotalSats(session);

                        await _botClient.SendMessage(
                            chatId: session.ChatId,
                            text: $"🎉🏆 *WINNER SELECTED!* 🏆🎉\n\n" +
                                  $"Congratulations {winner.DisplayName}!\n\n" +
                                  $"You won *{totalSats} sats*!\n\n" +
                                  $"Please create a Lightning invoice for {totalSats} sats and send it to me in a private message.\n\n" +
                                  $"You can also use the button on the previous lottery message.",
                            parseMode: ParseMode.Markdown,
                            cancellationToken: cancellationToken);

                        _logger.LogInformation("Winner selected for chat {ChatId}: user {UserId}", session.ChatId, winnerUserId);

                        await MessageHelper.UpdatePinnedStatusAsync(session, _botClient, _workflow, _logger, cancellationToken);
                    }
                    break;

                case SessionPhase.WaitingForPayout:
                    if (!session.PayoutCompleted && session.PayoutScheduledAt.HasValue && now >= session.PayoutScheduledAt.Value)
                    {
                        _logger.LogInformation("Payout delay elapsed for chat {ChatId}", session.ChatId);
                        session.PayoutCompleted = true;
                        session.PayoutExecutedAt = now;
                        await _botClient.SendMessage(
                            chatId: session.ChatId,
                            text: "⚡ Payout delay elapsed. Initiating payout to the winner!",
                            cancellationToken: cancellationToken);

                        await MessageHelper.UpdatePinnedStatusAsync(session, _botClient, _workflow, _logger, cancellationToken);
                    }
                    break;
            }
        }
    }
}
