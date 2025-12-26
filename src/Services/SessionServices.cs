using Telegram.Bot.Types.ReplyMarkups;
using teamZaps.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using teamZaps.Services;
using teamZaps.Backend;
using teamZaps.Utils;

namespace teamZaps.Session;

public class SessionWorkflowService
{
    public SessionWorkflowService(SessionManager sessionManager, IOptions<BotBehaviorOptions> botBehaviour)
    {
        this.sessionManager = sessionManager;
        this.botBehaviour = botBehaviour.Value;
    }


    public SessionState? GetSessionByChat(long chatId) => sessionManager.GetSessionByChat(chatId);
    public SessionState? GetSessionByUser(long userId) => sessionManager.GetSessionByUser(userId);
    public bool TryGetSession(long chatId, [NotNullWhen(true)] out SessionState? session)
    {
        session = sessionManager.GetSessionByChat(chatId);
        return (session is not null);
    }
    public bool TryGetSessionByUser(long userId, [NotNullWhen(true)] out SessionState? session)
    {
        session = sessionManager.GetSessionByUser(userId);
        return (session is not null);
    }
    public SessionState? TryStartSession(ChatFullInfo chat, User user) => sessionManager.TryCreateSession(chat, user);
    public bool TryCloseSession(long chatId, bool cancel) => sessionManager.RemoveSession(chatId, cancel);

    public ParticipantState EnsureParticipant(SessionState session, User user) => sessionManager.GetOrAddParticipant(session, user);


    private readonly SessionManager sessionManager;
    private readonly BotBehaviorOptions botBehaviour;
}

public class SessionManager : IFormattableAmount
{
    public SessionManager(ILogger<SessionManager> logger, IOptions<BotBehaviorOptions> botBehaviour, RecoveryService recoveryService)
    {
        this.logger = logger;
        this.botBehaviour = botBehaviour.Value;
        this.recoveryService = recoveryService;
    }


    #region Properties.Management
    public IEnumerable<SessionState> ActiveSessions => sessions.Values;
    public ConcurrentDictionary<long, PendingJoinInfo> PendingJoins { get; } = new();
    #endregion
    #region Properties
    public double? AvailableServerBudget => (botBehaviour.MaxBudget - ConsumedServerBudget);
    public double ConsumedServerBudget => ActiveSessions
        .SelectMany(s => s.LotteryParticipants.Values)
        .Sum();
    long IFormattableAmount.SatsAmount => TotalLockedSats;
    public long TotalLockedSats => ActiveSessions.Sum(p => p.SatsAmount);
    double IFormattableAmount.FiatAmount => TotalLockedFiat;
    public double TotalLockedFiat => ActiveSessions.Sum(p => p.FiatAmount);
    #endregion


    #region Events
    public event EventHandler? OnFirstSessionCreated;
    public event EventHandler? OnLastSessionRemoved;
    #endregion


    #region Management
    public SessionState? TryCreateSession(ChatFullInfo chat, User user)
    {
        // Check if maximum parallel sessions limit is reached:
        if (botBehaviour.MaxParallelSessions is not null)
        {
            var activeSessionCount = (uint)sessions.Count;
            if (activeSessionCount >= botBehaviour.MaxParallelSessions.Value)
            {
                logger.LogWarning("Refused to create session: Limit of {Limit} parallel sessions reached", botBehaviour.MaxParallelSessions.Value);
                throw new InvalidOperationException($"Sorry, we reached the server's capacity! All session slots are in use. Please try again later.");
            }
        }

        var firstSession = sessions.IsEmpty();
        var startedByUser = new ParticipantState(user);
        
        var session = new SessionState
        {
            ChatId = chat.Id,
            ChatTitle = (chat.Title ?? ""),
            StartedByUser = startedByUser,
            Phase = SessionPhase.WaitingForLotteryParticipants
        };

        if (sessions.TryAdd(chat.Id, session))
        {
            logger.LogInformation("Session {Session} created by user {User}", session, user);
            if (firstSession)
                OnFirstSessionCreated?.Invoke(this, EventArgs.Empty);
            return (session);
        }
        else
            return (null);
    }

    public SessionState? GetSessionByChat(long chatId) => sessions.TryGetValue(chatId, out var session) ? session : null;
    public SessionState? GetSessionByUser(long userId) => ActiveSessions
        .FirstOrDefault(s => s.Participants.ContainsKey(userId));

    public bool RemoveSession(long chatId, bool cancel)
    {
        var removed = sessions.TryRemove(chatId, out var session);
        if (removed)
        {
            logger.LogInformation("Session {Session} removed", session);

            if (session is not null)
            {
                session.Close(cancel);
                
                if (!cancel)
                    // Clear recovery files for all participants:
                    recoveryService.ClearLostSats(session);
                
                lastSummaries[chatId] = new SessionSummary(
                    session.StartedAtBlock,
                    DateTimeOffset.Now,
                    session.SatsAmount,
                    session.FiatAmount,
                    session.Participants.Count,
                    session.WinnerUser?.UserId,
                    session.WinnerUser?.DisplayName(),
                    session.PayoutCompleted);
            }

            if (sessions.IsEmpty())
                OnLastSessionRemoved?.Invoke(this, EventArgs.Empty);
        }
        return (removed);
    }
    public ParticipantState GetOrAddParticipant(SessionState session, User user) => session.Participants.GetOrAdd(user.Id, uid => new ParticipantState(user)
    {
        Tip = botBehaviour.TipChoices.Min() // Default to the lowest tip choice
    });
    #endregion
    

    private readonly ConcurrentDictionary<long, SessionState> sessions = new();
    private readonly ConcurrentDictionary<long, SessionSummary> lastSummaries = new();
    private readonly BotBehaviorOptions botBehaviour;
    private readonly ILogger<SessionManager> logger;
    private readonly RecoveryService recoveryService;
}

public record SessionSummary(
    IBlockHeader StartedAtBlock,
    DateTimeOffset EndedAt,
    long TotalSats,
    double TotalFiat,
    int ParticipantCount,
    long? WinnerUserId,
    string? WinnerDisplayName,
    bool PayoutCompleted
);