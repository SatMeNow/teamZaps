using Telegram.Bot.Types.ReplyMarkups;
using teamZaps.Configuration;

namespace teamZaps.Sessions;

public class SessionWorkflowService
{
    public SessionWorkflowService(SessionManager sessionManager, IOptions<BotBehaviorOptions> options)
    {
        this.sessionManager = sessionManager;
        this.Options = options.Value;
    }


    public BotBehaviorOptions Options { init; get; }


    public SessionState? GetSessionByChat(long chatId) => sessionManager.GetSessionByChat(chatId);
    public SessionState? GetSessionByUser(long userId) => sessionManager.GetSessionByUser(userId);

    public bool TryStartSession(ChatFullInfo chat, long userId, string displayName, out SessionState session)
    {
        return sessionManager.TryCreateSession(chat, userId, displayName, out session);
    }

    public bool TryCloseSession(long chatId, bool cancel) => sessionManager.RemoveSession(chatId, cancel);

    public ParticipantState EnsureParticipant(SessionState session, long userId, string displayName)
    {
        return sessionManager.GetOrAddParticipant(session, userId, displayName);
    }


    private readonly SessionManager sessionManager;
}

public static class CallbackActions
{
    public const string JoinLottery = "joinLottery";
    public const string ViewStatus = "viewStatus";
    public const string JoinSession = "joinSession";
    public const string CloseSession = "closeSession";
    public const string CancelSession = "cancelSession";
    public const string MakePayment = "makePayment";
    public const string SelectBudget = "budget";
}
