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

    public bool TryCloseSession(long chatId) => sessionManager.RemoveSession(chatId);

    public ParticipantState EnsureParticipant(SessionState session, long userId, string displayName)
    {
        return sessionManager.GetOrAddParticipant(session, userId, displayName);
    }

    public InlineKeyboardMarkup? BuildSessionJoinKeyboard(SessionState session, long userId)
    {
        if (session.Phase == SessionPhase.Closed)
            return null;
        else
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
    }


    private readonly SessionManager sessionManager;
}

public static class CallbackActions
{
    public const string JoinLottery = "join_lottery";
    public const string ViewStatus = "view_status";
    public const string JoinSession = "join_session";
    public const string CloseSession = "close_session";
    public const string CancelSession = "cancel_session";
}
