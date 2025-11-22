using System.Collections.Concurrent;
using teamZaps.Configuration;

namespace teamZaps.Sessions;


public class SessionManager
{
    public SessionManager(ILogger<SessionManager> logger, IOptions<BotBehaviorOptions> options)
    {
        this.logger = logger;
        this.Options = options.Value;
    }


    public BotBehaviorOptions Options { init; get; }



    public bool TryCreateSession(ChatFullInfo chat, long userId, string userDisplayName, out SessionState session)
    {
        session = new SessionState
        {
            ChatId = chat.Id,
            ChatTitle = (chat.Title ?? ""),
            StartedByUserId = userId,
            StartedAt = DateTimeOffset.UtcNow,
            Phase = SessionPhase.WaitingForLotteryParticipants
        };

        if (sessions.TryAdd(chat.Id, session))
        {
            logger.LogInformation("Session created for chat {ChatId} by {UserId}", chat.Id, userId);
            return true;
        }

        session = GetSessionByChat(chat.Id)!;
        return false;
    }

    public SessionState? GetSessionByChat(long chatId) => sessions.TryGetValue(chatId, out var session) ? session : null;
    public SessionState? GetSessionByUser(long userId) => ActiveSessions
        .FirstOrDefault(s => s.Participants.ContainsKey(userId));

    public bool RemoveSession(long chatId)
    {
        var removed = sessions.TryRemove(chatId, out var session);
        if (removed)
        {
            logger.LogInformation("Session removed for chat {ChatId}", chatId);

            if (session is not null)
            {
                session.Close();
                
                lastSummaries[chatId] = new SessionSummary(
                    session.StartedAt,
                    DateTimeOffset.UtcNow,
                    GetTotalSats(session),
                    session.Participants.Count,
                    session.WinnerUserId,
                    session.WinnerUserId.HasValue ? session.Participants[session.WinnerUserId.Value].DisplayName : null,
                    session.PayoutCompleted);
            }
        }
        return removed;
    }
    public ParticipantState GetOrAddParticipant(SessionState session, long userId, string displayName)
    {
        return session.Participants.GetOrAdd(userId, uid => new ParticipantState
        {
            UserId = uid,
            DisplayName = displayName
        });
    }

    public long GetTotalSats(SessionState session) => session.ConfirmedPayments.Sum(p => p.AmountSats);

    public IReadOnlyCollection<SessionState> ActiveSessions => sessions.Values.ToList().AsReadOnly();

    public SessionSummary? GetLastSummary(long chatId)
    {
        if (lastSummaries.TryGetValue(chatId, out var summary))
            return summary;

        return null;
    }
    

    private readonly ConcurrentDictionary<long, SessionState> sessions = new();
    private readonly ConcurrentDictionary<long, SessionSummary> lastSummaries = new();
    private readonly ILogger<SessionManager> logger;
}

public record SessionSummary(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long TotalCollectedSats,
    int ParticipantCount,
    long? WinnerUserId,
    string? WinnerDisplayName,
    bool PayoutCompleted
);
