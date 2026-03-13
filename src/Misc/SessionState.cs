using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using TeamZaps.Backends;
using TeamZaps.Configuration;
using TeamZaps.Handlers;
using TeamZaps.Payment;
using TeamZaps.Services;
using TeamZaps.Utils;
using Telegram.Bot.Types;

namespace TeamZaps.Session;


public enum SessionPhase
{
    [Description("⏳ Waiting for lottery entries")]
    WaitingForLotteryParticipants,
    [Description("📋 Accepting orders")]
    AcceptingOrders,
    [Description("⏳ Waiting for payments")]
    WaitingForPayments,
    [Description("⌛ Waiting for winner invoice(s)")]
    WaitingForInvoice,

    [Description("❌ Canceled")]
    Canceled,
    [Description("✅ Completed")]
    Completed
}

public class SessionState : ITipableAmount, IOrderableAmount
{
    #region Constants.Settings
    static readonly TimeSpan ForceCloseMinPaymentPhaseDuration = TimeSpan.FromMinutes(3);
    #endregion


    public bool IsValid => (Duration is not null);
    public bool CanForceClose => (RemainingForceCloseTime == TimeSpan.Zero);
    public TimeSpan RemainingForceCloseTime => ((Phase == SessionPhase.WaitingForPayments) && (TimeInCurrentPhase < ForceCloseMinPaymentPhaseDuration)) ? (ForceCloseMinPaymentPhaseDuration - TimeInCurrentPhase) : TimeSpan.Zero;
    
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

    public SessionPhase Phase
    {
        get => phase;
        set
        {
            phase = value;
            phaseChangeTime = DateTime.UtcNow;
        }
    }
    private SessionPhase phase = default;
    private DateTime phaseChangeTime = DateTime.UtcNow;
    public TimeSpan TimeInCurrentPhase => (DateTime.UtcNow - phaseChangeTime);

    public int? StatusMessageId { get; set; }
    public PendingChatWelcome? PendingWelcome { get; set; }

    public ConcurrentDictionary<long, ParticipantState> Participants { get; } = new();
    public ConcurrentDictionary<string, PendingPayment> PendingPayments { get; } = new();
    public IEnumerable<PaymentRecord> Payments => Participants.Values.SelectMany(p => p.Payments);
    public bool HasPayments => !Payments.IsEmpty();
    public long SatsAmount => Payments.Sum(p => p.SatsAmount);
    public double FiatAmount => Payments.Sum(p => p.FiatAmount);
    public double TipAmount => Payments.Sum(p => p.TipAmount);
    /// <summary>
    /// Tip amount in sats.
    /// </summary>
    public long TipSatsAmount => (long)(SatsAmount * TipAmount / FiatAmount);
    /// <summary>
    /// Max. budget, based on lottery participants' budgets.
    /// </summary>
    public double Budget => LotteryParticipants.Values.Sum();
    /// <summary>
    /// Remaining budget after subtracting already collected orders' amount.
    /// </summary>
    public double RemainingBudget => (Budget - OrdersFiatAmount);

    public bool HasOrders => Participants.Values.Any(p => p.HasOrders);
    public IEnumerable<OrderRecord> Orders => Participants.Values.SelectMany(p => p.Orders);
    double IOrderableAmount.FiatAmount => OrdersFiatAmount;
    public double OrdersFiatAmount => Orders.Sum(o => o.FiatAmount);
    double IOrderableAmount.TipAmount => OrdersTipAmount;
    public double OrdersTipAmount => Orders.Sum(o => o.TipAmount);
    
    public Dictionary<ParticipantState, double> LotteryParticipants { get; } = new(); // User -> MaxBudget
    public Dictionary<ParticipantState, PayableFiatAmount> WinnerPayouts { get; } = new(); // User -> Payout info
    public ICollection<ParticipantState> Winners => WinnerPayouts.Keys;
    public ParticipantState? Winner => WinnerPayouts.Keys.FirstOrDefault();
    public long PayedAmount => WinnerPayouts.Values.Sum(p => p.PayedAmount);
    public double PayedFiatAmount => WinnerPayouts.Values.Sum(p => p.PayedFiatAmount);
    public bool PayoutCompleted => WinnerPayouts.Values.All(w => w.PaymentCompleted);
    public int? WinnerMessageId { get; set; }

    public SessionStatistics? Statistics { get; set; }


    public override string ToString() => $"{ChatTitle} ({ChatId})";
    public static implicit operator long(SessionState session) => session.ChatId;


    #region Management
    public void Close(bool cancel)
    {
        Phase = (cancel ? SessionPhase.Canceled : SessionPhase.Completed);
    }
    #endregion
}

public record OrderRecord
{
    public required PaymentToken[] Tokens;
    public required double FiatAmount;
    public required double TipAmount;
    public required DateTimeOffset Timestamp;


    public void RemoveToken(int index, byte? tip)
    {
        Tokens = Tokens.Where((_, i) => i != index).ToArray();
        
        // Recalculate amounts after token removal
        var gross = (double)Tokens.Sum(t => t.Amount);
        TipAmount = (tip > 0) ? Math.Round(gross * tip.Value / 100.0, 2) : 0.0;
        FiatAmount = Math.Round(gross + TipAmount, 2);
    }
}

public record PendingJoinInfo(long ChatId);
public record PendingChatWelcome(int MessageId, List<User> PendingUsers);
public record PendingEditToken(int OrderIndex, int TokenIndex, int? PromptMessageId);

public class ParticipantState : IUser, ITipableAmount, IOrderableAmount
{
    public ParticipantState(User user) : this(user, new()) {}
    public ParticipantState(User user, BotUserOptions options)
    {
        this.User = user;
        this.Options = options;
    }


    public User User { get; }
    public long UserId => User.Id;
    public BotUserOptions Options { get; }

    public List<OrderRecord> Orders { get; } = new();
    public bool HasOrders => (Orders.Count > 0);
    double IOrderableAmount.FiatAmount => OrdersFiatAmount;
    public double OrdersFiatAmount => Orders.Sum(o => o.FiatAmount);
    double IOrderableAmount.TipAmount => OrdersTipAmount;
    public double OrdersTipAmount => Orders.Sum(o => o.TipAmount);

    public List<PaymentRecord> Payments { get; } = new();
    public bool HasPayments => (Payments.Count > 0);
    public long SatsAmount => Payments.Sum(p => p.SatsAmount);
    public double TipAmount => Payments.Sum(p => p.TipAmount);
    public double FiatAmount => Payments.Sum(p => p.FiatAmount);

    public int? StatusMessageId { get; set; }
    public int? PaymentHelpMessageId { get; set; }
    public int? OrderConfirmationMessageId { get; set; }
    public int? BudgetSelectionMessageId { get; set; }
    public int? TipSelectionMessageId { get; set; }
    public int? EditPickerMessageId { get; set; }
    public PendingEditToken? PendingEdit { get; set; }


    public override string ToString() => User.ToString();
    public static implicit operator long(ParticipantState user) => user.UserId;
    public static implicit operator User(ParticipantState user) => user.User;


    public bool JoinedLottery(SessionState session) => session.LotteryParticipants.ContainsKey(this);
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

public class PendingPayment : IUser, ITipableAmount, IOrderableAmount
{
    public required ParticipantState Participant { get; init; }
    public User User => Participant.User;
    public long UserId => User.Id;

    public required string PaymentHash { get; init; }
    public required string PaymentRequest { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public bool NotifiedPaid { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public int? MessageId { get; set; }
    
    public required PaymentToken[] Tokens { get; init; }
    public required PaymentCurrency Currency { get; init; }
    long IFormattableAmount.SatsAmount => SatsAmount;
    public required long SatsAmount { get; init; }
    public required double FiatAmount { get; init; }
    /// <summary>
    /// Tip amount in fiat currency.
    /// </summary>
    /// <remarks>
    /// Included in the <see cref="FiatAmount"/>.
    /// </remarks>
    public required double TipAmount { get; init; }


    public override string ToString() => $"{User}: {(this as ITipableAmount).FormatAmount()}";
}

public class PayableAmount
{
    public PayableAmount(long satsAmount)
    {
        this.SatsAmount = satsAmount;
    }

    
    public IReadOnlyCollection<long> Payments => payments;
    private List<long> payments = new();
    public long PayedAmount => Payments.Sum();
    public long RemainingAmount => (SatsAmount - PayedAmount);
    public bool PaymentCompleted => (PayedAmount >= SatsAmount);

    public long SatsAmount { get; }


    public void AddPayment(long amount) => payments.Add(amount);
}
public class PayableFiatAmount : PayableAmount, IFormattableAmount
{
    public PayableFiatAmount(double fiatAmount, long satsAmount) : base(satsAmount)
    {
        this.FiatAmount = fiatAmount;
    }

    
    public double FiatAmount { get; }
    public double PayedFiatAmount => ((double)PayedAmount / SatsAmount * FiatAmount);
    public double RemainingFiatAmount => (FiatAmount - PayedFiatAmount);
}


internal static partial class Ext
{
    public static void AppendOrders(this StringBuilder source, IEnumerable<OrderRecord> orders)
    {
        foreach (var token in orders.SelectMany(p => p.Tokens))
            source.AppendLine($"  • {token}");
    }
    public static void AppendSessionState(this StringBuilder source, SessionState session)
    {
        var title = (session.SessionTitle ?? "*Session status*");

        source.AppendLine($"📊 {title}");
        source.AppendLine();
        source.AppendLine($"• Phase: *{session.Phase.GetDescription()}*");
        source.AppendLine($"• Started at block: {session.StartedAtBlock!.FormatHeight()}");
        source.AppendLine($"• Started at time: {session.StartedAtBlock!.LocalTime:g}"); // `31.10.2008 17:04`
    }
    public static bool IsClosed(this SessionPhase source) => ((source == SessionPhase.Canceled) || (source == SessionPhase.Completed));
}