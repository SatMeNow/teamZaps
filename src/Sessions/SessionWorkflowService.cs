using Telegram.Bot.Types.ReplyMarkups;
using teamZaps.Configuration;

namespace teamZaps.Sessions;

public class SessionWorkflowService
{
    private readonly SessionManager _sessionManager;
    private readonly BotBehaviorOptions _options;

    public SessionWorkflowService(SessionManager sessionManager, IOptions<BotBehaviorOptions> options)
    {
        _sessionManager = sessionManager;
        _options = options.Value;
    }

    public SessionState? GetSession(long chatId) => _sessionManager.GetSession(chatId);

    public bool TryStartSession(ChatFullInfo chat, long userId, string displayName, out SessionState session)
    {
        return _sessionManager.TryCreateSession(chat, userId, displayName, out session);
    }

    public bool TryCloseSession(long chatId) => _sessionManager.RemoveSession(chatId);

    public ParticipantState EnsureParticipant(SessionState session, long userId, string displayName)
    {
        return _sessionManager.GetOrAddParticipant(session, userId, displayName);
    }

    public ParticipantState AddPayment(SessionState session, long userId, string displayName, PaymentRecord payment)
    {
        var participant = _sessionManager.GetOrAddParticipant(session, userId, displayName);
        participant.Payments.Add(payment);
        participant.TotalPaidSats += payment.AmountSats;
        session.ConfirmedPayments.Add(payment);
        return participant;
    }

    public long TotalSats(SessionState session) => _sessionManager.GetTotalSats(session);

    public InlineKeyboardMarkup BuildLotteryKeyboard(SessionState session, bool alreadyJoined)
    {
        var joinButton = InlineKeyboardButton.WithCallbackData(alreadyJoined ? "✅ Joined" : "🎟️ Join Lottery", CallbackActions.JoinLottery);
        var infoButton = InlineKeyboardButton.WithCallbackData("ℹ️ Status", CallbackActions.ViewStatus);
        return new InlineKeyboardMarkup(new[] { joinButton, infoButton });
    }

    public InlineKeyboardMarkup BuildWinnerInvoiceKeyboard(SessionState session)
    {
        var submitInvoice = InlineKeyboardButton.WithCallbackData("📤 Submit Invoice", CallbackActions.SubmitInvoice);
        var viewStatus = InlineKeyboardButton.WithCallbackData("ℹ️ Status", CallbackActions.ViewStatus);
        return new InlineKeyboardMarkup(new[] { submitInvoice, viewStatus });
    }

    public InlineKeyboardMarkup BuildPayoutKeyboard(SessionState session, long voterUserId)
    {
        bool alreadyVoted = session.PayoutVotes.Contains(voterUserId);
        var voteButton = InlineKeyboardButton.WithCallbackData(alreadyVoted ? "✅ Voted" : "⚡ Payout Now", CallbackActions.VotePayout);
        var statusButton = InlineKeyboardButton.WithCallbackData("ℹ️ Status", CallbackActions.ViewStatus);
        return new InlineKeyboardMarkup(new[] { voteButton, statusButton });
    }

    public InlineKeyboardMarkup BuildSessionJoinKeyboard(SessionState session, long userId)
    {
        bool alreadyJoined = session.Participants.ContainsKey(userId);
        var joinButton = InlineKeyboardButton.WithCallbackData(alreadyJoined ? "✅ Joined" : "🎯 Join Session", CallbackActions.JoinSession);
        return new InlineKeyboardMarkup(new[] { joinButton });
    }

    public BotBehaviorOptions Options => _options;

    public SessionSummary? GetLastSummary(long chatId) => _sessionManager.GetLastSummary(chatId);
}

public static class CallbackActions
{
    public const string JoinLottery = "join_lottery";
    public const string ViewStatus = "view_status";
    public const string SubmitInvoice = "submit_invoice";
    public const string VotePayout = "vote_payout";
    public const string JoinSession = "join_session";
}
