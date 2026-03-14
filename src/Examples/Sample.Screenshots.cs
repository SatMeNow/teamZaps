using System.Reflection;
using Telegram.Bot.Types.ReplyMarkups;
using TeamZaps.Backends;
using TeamZaps.Configuration;
using TeamZaps.Handlers;
using TeamZaps.Payment;
using TeamZaps.Session;

namespace TeamZaps.Examples;

/// <summary>
/// Sends mocked session messages to the first root-user for screenshot purposes.
/// Each call sends one label message followed by the actual mocked message per screenshot.
/// </summary>
public class Sample_Screenshots
{
    public static async Task SendStatusScreenshotsAsync(ITelegramBotClient botClient, TelegramSettings telegramSettings, CancellationToken cancellationToken = default)
    {
        if (telegramSettings.RootUsers.Length == 0)
            throw new InvalidOperationException("No root users configured.");

        var rootUserId = telegramSettings.RootUsers[0];
        foreach (var (file, text, markup) in BuildScreenshots())
        {
            await botClient.SendMessage(
                chatId: rootUserId,
                text: $"📸 `{file}`",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await botClient.SendMessage(
                chatId: rootUserId,
                text: text,
                parseMode: ParseMode.Markdown,
                linkPreviewOptions: true,
                replyMarkup: markup,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<(string File, string Text, InlineKeyboardMarkup? Markup)> BuildScreenshots()
    {
        // Step 01 — Group status: session started, no lottery entries yet:
        var step01 = CreateStep01();
        yield return ("step-01-session-started", BuildGroupStatus(step01), BuildGroupKeyboard(step01));

        // Step 02a — Private: Alice's welcome message after joining:
        // Step 02b — Group status: participants joined, lottery not entered yet:
        var step02 = CreateStep02();
        var step02Alice = step02.Participants[Alice.Id];
        yield return ("step-02a-joined-private", BuildUserStatus(step02, step02Alice), BuildUserKeyboard(step02, step02Alice));
        yield return ("step-02b-group-status", BuildGroupStatus(step02), BuildGroupKeyboard(step02));

        // Step 03 — Group status: lottery entered, accepting orders:
        var step03 = CreateStep03();
        yield return ("step-03-lottery-entered", BuildGroupStatus(step03), BuildGroupKeyboard(step03));

        // Step 04 — Private: Bob's status after placing his orders:
        var step04 = CreateStep04();
        var step04Bob = step04.Participants[Bob.Id];
        yield return ("step-04-order-placed", BuildUserStatus(step04, step04Bob), BuildUserKeyboard(step04, step04Bob));

        // Step 05a — Group status: order phase closed, waiting for payments (🫰):
        // Step 05b — Private: Bob's Lightning invoice:
        var step05 = CreateStep05();
        yield return ("step-05a-group-status", BuildGroupStatus(step05), BuildGroupKeyboard(step05));
        yield return ("step-05b-invoice-private", BuildPaymentMessage(PendingPayment(step05.Participants[Bob.Id])), null);

        // Step 06 — Group status: Bob ✅ paid, Charlie 🫰 still pending:
        var step06 = CreateStep06();
        yield return ("step-06-invoice-received", BuildGroupStatus(step06), BuildGroupKeyboard(step06));

        // Step 07 — Group: winner announcement (Alice won):
        // Step 08 — Private: payment summary sent to Alice (winner):
        var step07 = CreateStep07();
        yield return ("step-07-winner-drawn", BuildWinnerMessage(step07), null);
        yield return ("step-08-winner-invoice", BuildSummaryMessage(step07), null);

        // Step 09 — Group status: session completed, payout done:
        var step09 = CreateStep09();
        yield return ("step-09-completed", BuildGroupStatus(step09), BuildGroupKeyboard(step09));
    }


    #region Users
    private static User Alice { get; } = new User { Id = 100000001, IsBot = false, FirstName = "Alice", Username = "alice_btc" };
    private static User Bob { get; } = new User { Id = 100000002, IsBot = false, FirstName = "Bob", Username = "bob_lightning" };
    private static User Charlie { get; } = new User { Id = 100000003, IsBot = false, FirstName = "Charlie", Username = "charlie_zaps" };
    private static ParticipantState NewAlice() => new(Alice, new BotUserOptions { Tip = 10 }); // 10% tip
    private static ParticipantState NewBob() => new(Bob, new BotUserOptions { Tip = 5 });     // 5% tip
    private static ParticipantState NewCharlie() => new(Charlie, new BotUserOptions { Tip = null }); // no tip
    #endregion


    #region Factories
    // Step 01: group status — session started, just Alice joined, no lottery entries:
    private static SessionState CreateStep01()
    {
        var alice = NewAlice();
        var session = CreateBaseSession(SessionPhase.WaitingForLotteryParticipants, alice);
        session.Participants.TryAdd(Alice.Id, alice);
        return (session);
    }

    // Step 02: group status — all three joined, no lottery entries:
    private static SessionState CreateStep02()
    {
        var alice = NewAlice();
        var bob = NewBob();
        var charlie = NewCharlie();
        var session = CreateBaseSession(SessionPhase.WaitingForLotteryParticipants, alice);
        session.Participants.TryAdd(Alice.Id, alice);
        session.Participants.TryAdd(Bob.Id, bob);
        session.Participants.TryAdd(Charlie.Id, charlie);
        return (session);
    }

    // Step 03: group status — all in lottery, accepting orders:
    private static SessionState CreateStep03()
    {
        var alice = NewAlice();
        var bob = NewBob();
        var charlie = NewCharlie();
        var session = CreateBaseSession(SessionPhase.AcceptingOrders, alice);
        session.Participants.TryAdd(Alice.Id, alice);
        session.Participants.TryAdd(Bob.Id, bob);
        session.Participants.TryAdd(Charlie.Id, charlie);
        session.LotteryParticipants.Add(alice, 100);
        session.LotteryParticipants.Add(bob, 150);
        return (session);
    }

    // Step 04: private — Bob with orders placed, AcceptingOrders phase:
    private static SessionState CreateStep04()
    {
        var alice = NewAlice();
        var bob = NewBob();
        var charlie = NewCharlie();
        var session = CreateBaseSession(SessionPhase.AcceptingOrders, alice);
        session.Participants.TryAdd(Alice.Id, alice);
        session.Participants.TryAdd(Bob.Id, bob);
        session.Participants.TryAdd(Charlie.Id, charlie);
        session.LotteryParticipants.Add(alice, 100);
        session.LotteryParticipants.Add(bob, 150);
        bob.Orders.Add(OrderRecord(bob, BobTokens));
        return (session);
    }

    // Step 05: group status + invoice — WaitingForPayments, Bob+Charlie ordered, no payments yet (🫰):
    private static SessionState CreateStep05()
    {
        var alice = NewAlice();
        var bob = NewBob();
        var charlie = NewCharlie();
        var session = CreateBaseSession(SessionPhase.WaitingForPayments, alice);
        session.Participants.TryAdd(Alice.Id, alice);
        session.Participants.TryAdd(Bob.Id, bob);
        session.Participants.TryAdd(Charlie.Id, charlie);
        session.LotteryParticipants.Add(alice, 100);
        session.LotteryParticipants.Add(bob, 150);
        bob.Orders.Add(OrderRecord(bob, BobTokens));
        charlie.Orders.Add(OrderRecord(charlie, CharlieTokens));
        return (session);
    }

    // Step 06: group status — WaitingForPayments, Bob ✅ paid, Charlie 🫰 still pending:
    private static SessionState CreateStep06()
    {
        var alice = NewAlice();
        var bob = NewBob();
        var charlie = NewCharlie();
        var session = CreateBaseSession(SessionPhase.WaitingForPayments, alice);
        session.Participants.TryAdd(Alice.Id, alice);
        session.Participants.TryAdd(Bob.Id, bob);
        session.Participants.TryAdd(Charlie.Id, charlie);
        session.LotteryParticipants.Add(alice, 100);
        session.LotteryParticipants.Add(bob, 150);
        var bobOrder = OrderRecord(bob, BobTokens);
        bob.Orders.Add(bobOrder);
        charlie.Orders.Add(OrderRecord(charlie, CharlieTokens));
        bob.Payments.Add(new PaymentRecord
        {
            User = bob.User,
            PaymentHash = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4",
            PaymentRequest = RandomPaymentRequest(),
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
            Tokens = bobOrder.Tokens,
            SatsAmount = 18000,
            TipAmount = bobOrder.TipAmount,
            FiatAmount = bobOrder.FiatAmount
        });
        return (session);
    }

    // Step 07 + 08: winner drawn — Alice won, all payments received:
    private static SessionState CreateStep07()
    {
        var alice = NewAlice();
        var bob = NewBob();
        var charlie = NewCharlie();
        var session = CreateBaseSession(SessionPhase.WaitingForInvoice, alice);
        session.Participants.TryAdd(Alice.Id, alice);
        session.Participants.TryAdd(Bob.Id, bob);
        session.Participants.TryAdd(Charlie.Id, charlie);
        session.LotteryParticipants.Add(alice, 100);
        session.LotteryParticipants.Add(bob, 150);
        var bobOrder = OrderRecord(bob, BobTokens);
        var charlieOrder = OrderRecord(charlie, CharlieTokens);
        bob.Orders.Add(bobOrder);
        charlie.Orders.Add(charlieOrder);
        bob.Payments.Add(new PaymentRecord { User = bob.User, PaymentHash = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4", PaymentRequest = RandomPaymentRequest(), Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10), Tokens = bobOrder.Tokens, SatsAmount = 18000, TipAmount = bobOrder.TipAmount, FiatAmount = bobOrder.FiatAmount });
        charlie.Payments.Add(new PaymentRecord { User = charlie.User, PaymentHash = "b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5", PaymentRequest = RandomPaymentRequest(), Timestamp = DateTimeOffset.UtcNow.AddMinutes(-8), Tokens = charlieOrder.Tokens, SatsAmount = 22000, TipAmount = charlieOrder.TipAmount, FiatAmount = charlieOrder.FiatAmount });
        // Alice is the lottery winner:
        session.WinnerPayouts.Add(alice, new PayableFiatAmount(12.00, 40000));
        return (session);
    }

    // Step 09: session completed — winner paid out:
    private static SessionState CreateStep09()
    {
        var session = CreateStep07();
        session.Phase = SessionPhase.Completed;
        session.WinnerPayouts[session.Winner!].AddPayment(40000);
        session.CompletedAtBlock = new BlockHeader
        {
            Height = 210012,
            Hash = "000000000000048b95347e83192f69cf0366076336c639f9b7228e9ba171343f",
            BlockTime = new DateTimeOffset(2012, 11, 28, 18, 44, 38, TimeSpan.Zero)
        };
        return (session);
    }
    #endregion


    #region Order data
    private static readonly PaymentToken[] BobTokens =
    [
        new PaymentToken { Amount = 3.50m, Currency = PaymentCurrency.Euro, Note = "Cappuccino" },
        new PaymentToken { Amount = 2.00m, Currency = PaymentCurrency.Euro, Note = "Croissant" },
    ];
    private static readonly PaymentToken[] CharlieTokens =
    [
        new PaymentToken { Amount = 4.00m, Currency = PaymentCurrency.Euro, Note = "Latte" },
        new PaymentToken { Amount = 2.50m, Currency = PaymentCurrency.Euro, Note = "Muffin" },
    ];
    private static OrderRecord OrderRecord(ParticipantState participant, PaymentToken[] tokens)
    {
        var fiatAmount = (double)tokens.Sum(t => t.Amount);
        var tipAmount = Math.Round(fiatAmount * (participant.Options.Tip ?? 0) / 100.0, 2);
        return new()
        {
            Tokens = tokens,
            FiatAmount = fiatAmount,
            TipAmount = tipAmount,
            Timestamp = DateTimeOffset.UtcNow
        };
    }
    private static PendingPayment PendingPayment(ParticipantState participant)
    {
        var order = OrderRecord(participant, BobTokens);
        return new()
        {
            Participant = participant,
            PaymentHash = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4",
            PaymentRequest = RandomPaymentRequest(),
            CreatedAt = DateTimeOffset.UtcNow,
            Tokens = order.Tokens,
            Currency = PaymentCurrency.Euro,
            SatsAmount = 18000,
            FiatAmount = order.FiatAmount,
            TipAmount = order.TipAmount
        };
    }
    #endregion


    #region Helpers
    private static readonly MethodInfo GroupStatusBuild = typeof(SessionStatusMessage).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo GroupKeyboardBuild = typeof(SessionStatusMessage).GetMethod("BuildKeyboard", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo UserStatusBuild = typeof(UserStatusMessage).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo UserKeyboardBuild = typeof(UserStatusMessage).GetMethod("BuildKeyboard", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo PaymentBuild = typeof(LightningPaymentMessage).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo WinnerBuild = typeof(WinnerMessage).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo SummaryBuild = typeof(SessionSummaryMessage).GetMethod("BuildSummary", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string BuildGroupStatus(SessionState session) => (string)GroupStatusBuild.Invoke(null, [session])!;
    private static InlineKeyboardMarkup? BuildGroupKeyboard(SessionState session) => (InlineKeyboardMarkup?)GroupKeyboardBuild.Invoke(null, [session, 0L]);
    private static string BuildUserStatus(SessionState session, ParticipantState participant) => (string)UserStatusBuild.Invoke(null, [session, participant])!;
    private static InlineKeyboardMarkup? BuildUserKeyboard(SessionState session, ParticipantState? participant = null) => (InlineKeyboardMarkup?)UserKeyboardBuild.Invoke(null, [session, participant]);
    private static string BuildPaymentMessage(PendingPayment payment) => (string)PaymentBuild.Invoke(null, [payment, PaymentStatus.Pending])!;
    private static string BuildWinnerMessage(SessionState session) => (string)WinnerBuild.Invoke(null, [session, null, PaymentStatus.Pending, null])!;
    private static string BuildSummaryMessage(SessionState session) => (string)SummaryBuild.Invoke(null, [session, session.WinnerPayouts.Values.First()])!;

    private static string RandomPaymentRequest()
    {
        const string bech32 = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
        var rng = new Random();
        var data = new char[rng.Next(230, 270)];
        for (var i = 0; i < data.Length; i++)
            data[i] = bech32[rng.Next(bech32.Length)];
        return ($"lnbc{rng.Next(1000, 99999)}n1{new string(data)}");
    }
    private static SessionState CreateBaseSession(SessionPhase phase, ParticipantState alice) => new()
    {
        ChatId = -100123456789,
        ChatTitle = "Bitcoin Coffee Club",
        StartedByUser = alice,
        StartedAtBlock = new BlockHeader
        {
            Height = 210000,
            Hash = "000000000000048b95347e83192f69cf0366076336c639f9b7228e9ba171342e",
            BlockTime = new DateTimeOffset(2012, 11, 28, 16, 24, 38, TimeSpan.Zero)
        },
        Phase = phase
    };
    #endregion
}
