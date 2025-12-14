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
    public bool TryGetSessionByUser(long userId, [NotNullWhen(true)] out SessionState? session)
    {
        session = sessionManager.GetSessionByUser(userId);
        return (session is not null);
    }
    public bool TryStartSession(ChatFullInfo chat, User user, out SessionState session) => sessionManager.TryCreateSession(chat, user, out session);
    public bool TryCloseSession(long chatId, bool cancel) => sessionManager.RemoveSession(chatId, cancel);

    public ParticipantState EnsureParticipant(SessionState session, User user) => sessionManager.GetOrAddParticipant(session, user);


    private readonly SessionManager sessionManager;
    private readonly BotBehaviorOptions botBehaviour;
}

public static class CallbackActions
{
    public const string JoinLottery = "joinLottery";
    public const string ViewStatus = "viewStatus";
    public const string JoinSession = "joinSession";
    public const string CloseSession = "closeSession";
    public const string CancelSession = "cancelSession";
    public const string MakePayment = "makePayment";
    public const string SelectBudget = "selectBudget";
    public const string SetTip = "setTip";
    public const string SelectTip = "selectTip";
    public const string RecoverCreate = "recover_create";
    public const string RecoverCancel = "recover_cancel";
    public const string RecoverInvoice = "recover_invoice";
}
