using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using teamZaps.Utils;

namespace teamZaps.Sessions;

public class SessionState : IFormattableAmount
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

    public DateTimeOffset? LotteryOpenedAt { get; set; }
    public DateTimeOffset? LotteryClosesAt { get; set; }
    public int? LotteryMessageId { get; set; }
    public HashSet<long> LotteryParticipants { get; } = new();
    public long? WinnerUserId { get; set; }
    public int? WinnerMessageId { get; set; }

    public string? WinnerInvoiceBolt11 { get; set; }
    public string? WinnerInvoiceHash { get; set; }
    public int? WinnerInvoiceMessageId { get; set; }
    public DateTimeOffset? InvoiceSubmittedAt { get; set; }

    public DateTimeOffset? PayoutExecutedAt { get; set; }
    public bool PayoutCompleted { get; set; }


    public void Close()
    {
        Phase = SessionPhase.Closed;
        PendingJoins.Clear();
    }
}

public class ParticipantState : IFormattableAmount
{
    public required long UserId { get; init; }
    public required string DisplayName { get; init; }

    public List<PaymentRecord> Payments { get; } = new();
    public bool HasPayments => (Payments.Count > 0);
    public long SatsAmount => (HasPayments ? Payments.Sum(p => p.SatsAmount) : 0);
    public double FiatAmount => (HasPayments ? Payments.Sum(p => p.FiatAmount) : 0.0F);

    public bool JoinedLottery { get; set; }
    public bool SubmittedInvoice { get; set; }
    public int? StatusMessageId { get; set; }
}

public record PaymentRecord() : IFormattableAmount
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
    public required double FiatAmount;
    public required double FiatRate;
}

public class PendingPayment : IFormattableAmount
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
    public required double FiatAmount { get; init; }
}

public enum SessionPhase
{
    [Description("⏳ Waiting for lottery entries")]
    WaitingForLotteryParticipants,
    [Description("💰 Accepting payments")]
    AcceptingPayments,
    [Description("⌛ Waiting for winner invoice submission")]
    WaitingForInvoice,
    [Description("❌ Closed")]
    Closed
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
        source.AppendLine($"Phase: *{session.Phase.GetDescription()}*");
        source.AppendLine($"Started: {session.StartedAt:yyyy-MM-dd HH:mm} UTC");
    }
}