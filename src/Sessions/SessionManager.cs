using System.Collections.Concurrent;
using teamZaps.Configuration;

namespace teamZaps.Sessions;


public class SessionManager
{
    private readonly ConcurrentDictionary<long, SessionState> _sessions = new();
    private readonly ConcurrentDictionary<long, SessionSummary> _lastSummaries = new();
    private readonly ILogger<SessionManager> _logger;
    private readonly BotBehaviorOptions _options;

    public SessionManager(ILogger<SessionManager> logger, IOptions<BotBehaviorOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public bool TryCreateSession(ChatFullInfo chat, long userId, string userDisplayName, out SessionState session)
    {
        session = new SessionState
        {
            ChatId = chat.Id,
            ChatTitle = (chat.Title ?? ""),
            StartedByUserId = userId,
            StartedAt = DateTimeOffset.UtcNow,
            Phase = SessionPhase.AcceptingPayments
        };

        if (_sessions.TryAdd(chat.Id, session))
        {
            _logger.LogInformation("Session created for chat {ChatId} by {UserId}", chat.Id, userId);
            return true;
        }

        session = GetSession(chat.Id)!;
        return false;
    }

    public SessionState? GetSession(long chatId) => _sessions.TryGetValue(chatId, out var session) ? session : null;

    public bool RemoveSession(long chatId)
    {
        var removed = _sessions.TryRemove(chatId, out var session);
        if (removed)
        {
            _logger.LogInformation("Session removed for chat {ChatId}", chatId);

            if (session is not null)
            {
                _lastSummaries[chatId] = new SessionSummary(
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

    public BotBehaviorOptions Options => _options;

    public ParticipantState GetOrAddParticipant(SessionState session, long userId, string displayName)
    {
        return session.Participants.GetOrAdd(userId, uid => new ParticipantState
        {
            UserId = uid,
            DisplayName = displayName
        });
    }

    public long GetTotalSats(SessionState session) => session.ConfirmedPayments.Sum(p => p.AmountSats);

    public IReadOnlyCollection<SessionState> ActiveSessions => _sessions.Values.ToList().AsReadOnly();

    public SessionSummary? GetLastSummary(long chatId)
    {
        if (_lastSummaries.TryGetValue(chatId, out var summary))
            return summary;

        return null;
    }
}

public record SessionSummary(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long TotalCollectedSats,
    int ParticipantCount,
    long? WinnerUserId,
    string? WinnerDisplayName,
    bool PayoutCompleted);
