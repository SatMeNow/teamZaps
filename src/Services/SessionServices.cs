using Telegram.Bot.Types.ReplyMarkups;
using TeamZaps.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using TeamZaps.Services;
using TeamZaps.Backend;
using TeamZaps.Utils;
using System.Diagnostics;

namespace TeamZaps.Session;

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
    public Task<SessionState?> TryStartSessionAsync(ChatFullInfo chat, CommandMessage command) => sessionManager.TryCreateSessionAsync(chat, command);
    public bool TryCloseSession(long chatId, bool cancel) => sessionManager.RemoveSession(chatId, cancel);

    public Task<ParticipantState> EnsureParticipantAsync(SessionState session, User user) => sessionManager.GetOrAddParticipantAsync(session, user);


    private readonly SessionManager sessionManager;
    private readonly BotBehaviorOptions botBehaviour;
}

public class SessionManager : IFormattableAmount
{
    public SessionManager(ILogger<SessionManager> logger, IOptions<BotBehaviorOptions> botBehaviour, FileService<BotUserOptions> userOptionsService, RecoveryService recoveryService)
    {
        this.logger = logger;
        this.botBehaviour = botBehaviour.Value;
        this.userOptionsService = userOptionsService;
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
    public async Task<SessionState?> TryCreateSessionAsync(ChatFullInfo chat, CommandMessage command)
    {
        // Check if maximum parallel sessions limit is reached:
        if (botBehaviour.MaxParallelSessions is not null)
        {
            var activeSessionCount = (uint)sessions.Count;
            if (activeSessionCount >= botBehaviour.MaxParallelSessions.Value)
            {
                logger.LogWarning("Refused to create session: Limit of {Limit} parallel sessions reached.", botBehaviour.MaxParallelSessions.Value);
                throw new InvalidOperationException($"Sorry, we reached the server's capacity! All session slots are in use. Please try again later.");
            }
        }

        // Parse customized session title (optional)
        string? sessionTitle = null;
        if (command.Arguments.Length > 0)
            sessionTitle = string.Join(" ", command.Arguments).ToMarkdownString();

        var user = command.From;
        var firstSession = sessions.IsEmpty();
        var startedByUser = await CreateParticipantAsync(user).ConfigureAwait(false);
        
        var session = new SessionState
        {
            ChatId = chat.Id,
            ChatTitle = (chat.Title ?? ""),
            SessionTitle = sessionTitle,
            StartedByUser = startedByUser,
            Phase = SessionPhase.WaitingForLotteryParticipants
        };

        if (sessions.TryAdd(chat.Id, session))
        {
            logger.LogInformation("Session {Session} created by user {User}.", session, user);
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
            logger.LogInformation("Session {Session} removed.", session);

            if (session is not null)
            {
                session.Close(cancel);
                
                if (!cancel)
                    // Clear recovery files for all participants:
                    recoveryService.ClearLostSats(session);
            }

            if (sessions.IsEmpty())
                OnLastSessionRemoved?.Invoke(this, EventArgs.Empty);
        }
        return (removed);
    }
    private async Task<ParticipantState> CreateParticipantAsync(User user)
    {
        // Load saved user options:
        var userOptions = await userOptionsService.ReadAsync(user.Id).ConfigureAwait(false);
        // Create default options if none exist:
        if (userOptions is null)
        {
            userOptions = new BotUserOptions
            {
                Tip = botBehaviour.TipChoices.Min() // Default to the lowest tip choice
            };
        }

        return (new ParticipantState(user, userOptions));
    }
    public async Task<ParticipantState> GetOrAddParticipantAsync(SessionState session, User user)
    {
        if (session.Participants.TryGetValue(user.Id, out var participant))
            return (participant);
        else
        {
            var newParticipant = await CreateParticipantAsync(user).ConfigureAwait(false);
            if (!session.Participants.TryAdd(user.Id, newParticipant))
                Debug.Assert(false);
            return (newParticipant);
        }
    }
    #endregion
    

    private readonly ConcurrentDictionary<long, SessionState> sessions = new();
    private readonly BotBehaviorOptions botBehaviour;
    private readonly FileService<BotUserOptions> userOptionsService;
    private readonly ILogger<SessionManager> logger;
    private readonly RecoveryService recoveryService;
}