using System.Collections.Concurrent;

namespace teamZaps.Sessions;

public class SessionState
{
    public required long ChatId { get; init; }
    public required string ChatTitle { get; init; }
    public required long StartedByUserId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }


    public SessionPhase Phase { get; set; } = SessionPhase.AcceptingPayments;

    public int? StatusMessageId { get; set; }

    public ConcurrentDictionary<long, ParticipantState> Participants { get; } = new();
    public ConcurrentDictionary<string, PendingPayment> PendingPayments { get; } = new();
    public List<PaymentRecord> ConfirmedPayments { get; } = new();

    public DateTimeOffset? LotteryOpenedAt { get; set; }
    public DateTimeOffset? LotteryClosesAt { get; set; }
    public int? LotteryMessageId { get; set; }
    public HashSet<long> LotteryParticipants { get; } = new();
    public long? WinnerUserId { get; set; }

    public string? WinnerInvoiceBolt11 { get; set; }
    public string? WinnerInvoiceHash { get; set; }
    public int? WinnerInvoiceMessageId { get; set; }
    public DateTimeOffset? InvoiceSubmittedAt { get; set; }

    public DateTimeOffset? PayoutScheduledAt { get; set; }
    public DateTimeOffset? PayoutExecutedAt { get; set; }
    public bool PayoutCompleted { get; set; }
    public int? CurrentPayoutActionMessageId { get; set; }
    public HashSet<long> PayoutVotes { get; } = new();
}

public class ParticipantState
{
    public required long UserId { get; init; }
    public required string DisplayName { get; init; }
    public List<PaymentRecord> Payments { get; } = new();
    public HashSet<string> PendingPaymentHashes { get; } = new();
    public long TotalPaidSats { get; set; }
    public bool JoinedLottery { get; set; }
    public bool SubmittedInvoice { get; set; }
}

public record PaymentRecord(
    long UserId,
    string DisplayName,
    long AmountSats,
    string Currency,
    string PaymentHash,
    string PaymentRequest,
    string InputExpression,
    DateTimeOffset Timestamp
);

public class PendingPayment
{
    public required string PaymentHash { get; init; }
    public required string PaymentRequest { get; init; }
    public required long UserId { get; init; }
    public required string DisplayName { get; init; }
    public required decimal Amount { get; init; }
    public required PaymentCurrency Currency { get; init; }
    public required string InputExpression { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public bool NotifiedPaid { get; set; }
    public long? SettledSats { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
}

public enum SessionPhase
{
    AcceptingPayments,
    LotteryOpen,
    WaitingForInvoice,
    WaitingForPayout,
    Closed
}
