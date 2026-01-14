using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using TeamZaps.Backend;
using TeamZaps.Configuration;
using TeamZaps.Services;
using TeamZaps.Statistic;
using TeamZaps.Utils;
using Telegram.Bot.Types;

namespace TeamZaps.Session;


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

public class SessionState : ITipableAmount
{
    public bool IsValid => (Duration is not null);
    
    public required long ChatId { get; init; }
    public required string ChatTitle { get; init; }
    public string? SessionTitle { get; init; }
    public string DisplayTitle => (SessionTitle ?? ChatTitle);

    public required ParticipantState StartedByUser { get; init; }
    public int? Duration => (CompletedAtBlock?.Height - StartedAtBlock?.Height + 1);
    public BlockHeader? StartedAtBlock { get; set; }
    public BlockHeader? CompletedAtBlock { get; set; }

    public BotAdminOptions AdminOptions { get; set; } = new();
    public bool BotCanPinMessages { get; set; }

    public SessionPhase Phase { get; set; } = SessionPhase.AcceptingPayments;

    public int? StatusMessageId { get; set; }

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

    public Dictionary<long, double> LotteryParticipants { get; } = new(); // UserId -> MaxBudget
    public Dictionary<long, WinnerInfo> Winners { get; } = new(); // UserId -> Winner info
    public IEnumerable<ParticipantState> WinnerUsers => Winners.Keys.Select(id => Participants[id]);
    public ParticipantState? WinnerUser => WinnerUsers.FirstOrDefault();
    public bool PayoutCompleted => WinnerUsers.All(u => u.SubmittedInvoice);
    public int? WinnerMessageId { get; set; }

    public SessionStatistics? Statistics { get; set; }


    public override string ToString() => $"{ChatTitle} ({ChatId})";
    public static implicit operator long(SessionState session) => session.ChatId;


    #region Management
    public void Close(bool cancel)
    {
        Phase = (cancel ? SessionPhase.Canceled : SessionPhase.Completed);
    }
    public ParticipantState? GetWinnerUser(long userId) => WinnerUsers.FirstOrDefault(u => u.UserId.Equals(userId));
    #endregion
}

public record PendingJoinInfo(long ChatId, int WelcomeMessageId);

public class ParticipantState : IUser, ITipableAmount
{
    public ParticipantState(User user)
    {
        this.User = user;
    }


    public User User { get; }
    public long UserId => User.Id;

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


    public override string ToString() => User.ToString();
    public static implicit operator long(ParticipantState user) => user.UserId;
    public static implicit operator User(ParticipantState user) => user.User;


    public bool JoinedLottery(SessionState session) => session.LotteryParticipants.ContainsKey(UserId);
}

public record PaymentRecord() : IUser, ITipableAmount
{
    public required User User { get; init;} 
    public long UserId => User.Id;
    
    public required string PaymentHash;
    public required string PaymentRequest;
    public required DateTimeOffset Timestamp;
    
    public required PaymentToken[] Tokens;
    long IFormattableAmount.SatsAmount => this.SatsAmount;
    public required long SatsAmount;
    double IFormattableAmount.FiatAmount => this.FiatAmount;
    double ITipableAmount.TipAmount => this.TipAmount;
    public required double TipAmount;
    public required double FiatAmount;


    public override string ToString() => $"{User}: {this}";
}

public class PendingPayment : IUser, ITipableAmount
{
    public required User User { get; init; }
    public long UserId => User.Id;

    public required string PaymentHash { get; init; }
    public required string PaymentRequest { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public bool NotifiedPaid { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public int? MessageId { get; set; }
    
    public required PaymentToken[] Tokens { get; init; }
    public required PaymentCurrency Currency { get; init; }
    long IFormattableAmount.SatsAmount => (SatsAmount ?? 0);
    public long? SatsAmount { get; init; }
    public required double TipAmount { get; init; }
    public required double FiatAmount { get; init; }


    public override string ToString() => $"{User}: {(this as ITipableAmount).FormatAmount()}";
}

public record WinnerInfo(double FiatAmount, long SatsAmount);


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
        var title = (session.SessionTitle ?? "Session status");

        source.AppendLine($"📊 *{title}*");
        source.AppendLine();
        source.AppendLine($"• Phase: *{session.Phase.GetDescription()}*");
        source.AppendLine($"• Started at block: {session.StartedAtBlock!.FormatHeight()}");
        source.AppendLine($"• Started at time: {session.StartedAtBlock!.LocalTime:g}"); // `31.10.2008 17:04`
    }
    public static bool IsClosed(this SessionPhase source) => ((source == SessionPhase.Canceled) || (source == SessionPhase.Completed));
}