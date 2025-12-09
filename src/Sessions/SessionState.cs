using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using teamZaps.Utils;

namespace teamZaps.Sessions;

public class SessionState : ITipableAmount
{
    public required long ChatId { get; init; }
    public required string ChatTitle { get; init; }
    public required string StartedByUser { get; init; }
    public required DateTimeOffset StartedAt { get; init; }


    public SessionPhase Phase { get; set; } = SessionPhase.AcceptingPayments;

    public int? StatusMessageId { get; set; }

    public ConcurrentDictionary<long, (long ChatId, int MessageId)> PendingJoins { get; } = new();
    public ConcurrentDictionary<long, ParticipantState> Participants { get; } = new();
    public ConcurrentDictionary<string, PendingPayment> PendingPayments { get; } = new();
    public IEnumerable<PaymentRecord> Payments => Participants.Values.SelectMany(p => p.Payments);
    public bool HasPayments => !Payments.IsEmpty();
    public long SatsAmount => Payments.Sum(p => p.SatsAmount);
    public double FiatAmount => Payments.Sum(p => p.FiatAmount);
    public double TipAmount => Payments.Sum(p => p.TipAmount);
    /// <summary>
    /// Max. budget, based on lottery participants' budgets.
    /// </summary>
    public double Budget => LotteryParticipants.Values.Sum();

    public DateTimeOffset? LotteryOpenedAt { get; set; }
    public DateTimeOffset? LotteryClosesAt { get; set; }
    public int? LotteryMessageId { get; set; }
    public Dictionary<long, double> LotteryParticipants { get; } = new(); // UserId -> MaxBudget
    public Dictionary<long, double> Winners { get; } = new(); // UserId -> Amount to pay
    public IEnumerable<ParticipantState> WinnerUsers => Winners.Keys.Select(id => Participants[id]);
    public ParticipantState? WinnerUser => WinnerUsers.FirstOrDefault();
    public bool PayoutCompleted => WinnerUsers.All(u => u.SubmittedInvoice);
    public int? WinnerMessageId { get; set; }


    public void Close(bool cancel)
    {
        Phase = (cancel ? SessionPhase.Canceled : SessionPhase.Completed);
        PendingJoins.Clear();
    }
    public ParticipantState? GetWinnerUser(long userId) => WinnerUsers.FirstOrDefault(u => u.UserId.Equals(userId));
}

public class ParticipantState : ITipableAmount
{
    public required long UserId { get; init; }
    public required string DisplayName { get; init; }

    public byte? Tip { get; set; }
    public List<PaymentRecord> Payments { get; } = new();
    public bool HasPayments => (Payments.Count > 0);
    public long SatsAmount => (HasPayments ? Payments.Sum(p => p.SatsAmount) : 0);
    public double TipAmount => (HasPayments ? Payments.Sum(p => p.TipAmount) : 0.0);
    public double FiatAmount => (HasPayments ? Payments.Sum(p => p.FiatAmount) : 0.0F);

    public bool SubmittedInvoice { get; set; }
    public int? StatusMessageId { get; set; }
    public int? PaymentHelpMessageId { get; set; }
    public int? BudgetSelectionMessageId { get; set; }
    public int? TipSelectionMessageId { get; set; }


    public override string ToString() => DisplayName;


    public bool JoinedLottery(SessionState session) => session.LotteryParticipants.ContainsKey(UserId);
}

public record PaymentRecord() : ITipableAmount
{
    public required long UserId;
    public required string DisplayName;
    public required string PaymentHash;
    public required string PaymentRequest;
    public required DateTimeOffset Timestamp;
    
    public required PaymentToken[] Tokens;
    long IFormattableAmount.SatsAmount => this.SatsAmount;
    public required long SatsAmount;
    double IFormattableAmount.FiatAmount => this.FiatAmount;
    public required double FiatRate;
    double ITipableAmount.TipAmount => this.TipAmount;
    public required double TipAmount;
    public required double FiatAmount;
}

public class PendingPayment : ITipableAmount
{
    public required string PaymentHash { get; init; }
    public required string PaymentRequest { get; init; }
    public required long UserId { get; init; }
    public required string DisplayName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public bool NotifiedPaid { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public int? MessageId { get; set; }
    
    public required PaymentToken[] Tokens { get; init; }
    public required PaymentCurrency Currency { get; init; }
    long IFormattableAmount.SatsAmount => 0;
    public required double TipAmount { get; init; }
    public required double FiatAmount { get; init; }
}

public enum SessionPhase
{
    [Description("⏳ Waiting for lottery entries")]
    WaitingForLotteryParticipants,
    [Description("💰 Accepting payments")]
    AcceptingPayments,
    [Description("⌛ Waiting for winner invoice")]
    WaitingForInvoice,

    [Description("❌ Canceled")]
    Canceled,
    [Description("✅ Completed")]
    Completed
}


internal static partial class Ext
{
    public static void AppendPayments(this StringBuilder source, IEnumerable<PaymentRecord> payments)
    {
        foreach (var token in payments.SelectMany(p => p.Tokens))
        {
            var memo = string.IsNullOrWhiteSpace(token.Note) ? "" : $" - {token.Note}";
            source.AppendLine($"  • {token.FormatAmount()}{memo}");
        }
    }
    public static void AppendSessionState(this StringBuilder source, SessionState session)
    {
        source.AppendLine("📊 *Session Status*\n");
        source.AppendLine($"• Phase: *{session.Phase.GetDescription()}*");
        source.AppendLine($"• Started: {session.StartedAt:yyyy-MM-dd HH:mm} UTC");
    }
    public static bool IsClosed(this SessionPhase source) => ((source == SessionPhase.Canceled) || (source == SessionPhase.Completed));
}