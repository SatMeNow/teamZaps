using Telegram.Bot.Types.ReplyMarkups;
using teamZaps.Configuration;
using System.Diagnostics.CodeAnalysis;

namespace teamZaps.Sessions;

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
