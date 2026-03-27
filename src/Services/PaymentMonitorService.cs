using System.Diagnostics;
using TeamZaps.Configuration;
using TeamZaps.Backends;
using TeamZaps.Session;
using TeamZaps.Logging;
using TeamZaps.Handlers;

namespace TeamZaps.Services;

public class PaymentMonitorService : BackgroundService
{
    public PaymentMonitorService(LiquidityLogService liquidityLogService, SessionManager sessionManager, ILightningBackend lightningBackend, IIndexerBackend indexerBackend, ITelegramBotClient botClient, ILogger<PaymentMonitorService> logger, SessionWorkflowService workflowService, RecoveryService recoveryService)
    {
        this.liquidityLogService = liquidityLogService;
        this.sessionManager = sessionManager;
        this.lightningBackend = lightningBackend;
        this.cashuBackend = lightningBackend as ICashuBackend;
        this.indexerBackend = indexerBackend;
        this.botClient = botClient;
        this.logger = logger;
        this.workflowService = workflowService;
        this.recoveryService = recoveryService;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckPendingPaymentsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during payment monitoring loop.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CheckPendingPaymentsAsync(CancellationToken cancellationToken)
    {
        foreach (var session in sessionManager.ActiveSessions)
        {
            foreach (var pending in session.PendingPayments.Values)
            {
                // Cashu token payments are push-based — no polling needed, skip them here.
                if (pending.PaymentRequest is null)
                    continue;

                try
                {
                    var status = await lightningBackend.CheckPaymentStatusAsync(pending.PaymentHash, cancellationToken).ConfigureAwait(false);
                    if (status is null)
                        continue;
                    if (status.Paid && !pending.NotifiedPaid)
                        await ConfirmPaymentAsync(session, pending, status.SatsAmount, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error checking payment status for {PaymentHash}.", pending.PaymentHash);
                }
            }
        }
    }

    /// <summary>
    /// Marks a pending payment as confirmed, records it, and triggers lottery draw if all paid.
    /// Called both by the Lightning poll loop and directly for Cashu token payments.
    /// </summary>
    public async Task ConfirmPaymentAsync(SessionState session, PendingPayment pending, long satsReceived, CancellationToken cancellationToken)
    {
        if (pending.NotifiedPaid)
            return;

        #if DEBUG
        if (satsReceived > 0)
            Debug.Assert(satsReceived == pending.SatsAmount);
        Debug.Assert(pending.Currency == BotBehaviorOptions.AcceptedFiatCurrency);
        #endif

        pending.NotifiedPaid = true;
        pending.PaidAt = DateTimeOffset.Now;

        // Update the payment message to show paid status
        await pending.UpdatePaymentMessageAsync(PaymentStatus.Paid, botClient, logger, cancellationToken).ConfigureAwait(false);
        var payment = new PaymentRecord()
        {
            User = pending.User,
            PaymentHash = pending.PaymentHash,
            PaymentRequest = pending.PaymentRequest,
            Timestamp = pending.PaidAt ?? DateTimeOffset.Now,
            Tokens = pending.Tokens,
            SatsAmount = satsReceived,
            FiatAmount = pending.FiatAmount,
            TipAmount = pending.TipAmount
        };

        session.PendingPayments.TryRemove(pending.PaymentHash, out _);

        var participant = await sessionManager.GetOrAddParticipantAsync(session, pending.User).ConfigureAwait(false);
        participant.Payments.Add(payment);

        // Record lost sats for crash recovery:
        await recoveryService.WriteLostSatsAsync(participant, $"Payment in session *{session.ChatTitle}*").ConfigureAwait(false);

        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        
        // Update user status messages for affected participant
        await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
        
        // Delete payment help message if it exists
        if (participant.PaymentHelpMessageId is not null)
        {
            await botClient.DeleteMessageAsync(participant.UserId, participant.PaymentHelpMessageId!.Value, cancellationToken).ConfigureAwait(false);
            participant.PaymentHelpMessageId = null;
        }

        // If all participant invoices are paid, draw the lottery:
        if ((session.Phase == SessionPhase.WaitingForPayments) && session.PendingPayments.IsEmpty)
            await DrawLotteryAsync(session, cancellationToken).ConfigureAwait(false);
        
        // Update log
        await liquidityLogService.LogAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DrawLotteryAsync(SessionState session, CancellationToken cancellationToken)
    {
        // Draw winners based on budget limits:
        LotteryHelper.SelectWinners(session);

        var winners = string.Join(", ", session.WinnerPayouts.Select(w => $"{w.Key} ({w.Value.FiatAmount.Format()})"));
        logger.LogInformation("Winner(s) selected for session {Session}: {Winners}.", session, winners);

        // Update recovery files (remove losers, add winner payouts):
        var losers = session.Participants.Values.Except(session.Winners);
        recoveryService.ClearLostSats(losers);
        foreach (var winner in session.WinnerPayouts)
            await recoveryService.WriteLostSatsAsync(winner.Key, winner.Value.SatsAmount, $"Winner payout for session *{session.ChatTitle}*").ConfigureAwait(false);

        // For each winner, attempt immediate Cashu payout if preferred and available.
        // Otherwise fall through to the BOLT11 invoice submission phase.
        var cashuPayoutFailed = false;
        if (cashuBackend is not null)
        {
            foreach (var (winner, payout) in session.WinnerPayouts)
            {
                if (winner.Options.PreferredPaymentMethod != PaymentMethod.Cashu)
                    continue;
                try
                {
                    var token = await cashuBackend.SendTokenAsync(payout.SatsAmount, cancellationToken).ConfigureAwait(false);
                    payout.AddPayment(payout.SatsAmount);

                    await botClient.SendMessage(winner.UserId,
                        $"🏆 *You won!* Here are your sats:\n\n`{token}`\n\n" +
                        $"Import this token into any Cashu wallet.\n" +
                        $"• Amount: *{payout.SatsAmount.Format()}* ({payout.FiatAmount.Format()})",
                        parseMode: ParseMode.Markdown,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    recoveryService.ClearLostSats([winner]);
                    logger.LogInformation("Cashu payout sent to winner {Winner} for session {Session}: {Sats} sats.", winner, session, payout.SatsAmount);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Cashu payout failed for winner {Winner} in session {Session}, falling back to Lightning.", winner, session);
                    cashuPayoutFailed = true;
                }
            }
        }

        // If all payouts completed via Cashu, close session immediately.
        if (session.PayoutCompleted)
        {
            session.Phase = SessionPhase.Completed;
            var currentBlock = await indexerBackend.GetCurrentBlockAsync(cancellationToken).ConfigureAwait(false);
            session.CompletedAtBlock = currentBlock;
        }
        else
        {
            // One or more winners still need to submit a BOLT11.
            session.Phase = SessionPhase.WaitingForInvoice;
        }

        // Send winner notifications:
        await SessionSummaryMessage.SendAsync(botClient, logger, session, cashuBackend is not null, cancellationToken).ConfigureAwait(false);
        await WinnerMessage.SendAsync(session, botClient, workflowService, cancellationToken).ConfigureAwait(false);
        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);

        // Update user status messages for all participants:
        foreach (var p in session.Participants.Values)
            await UserStatusMessage.UpdateAsync(session, p, botClient, workflowService, logger, cancellationToken).ConfigureAwait(false);
    }

    private readonly LiquidityLogService liquidityLogService;
    private readonly SessionManager sessionManager;
    private readonly ILightningBackend lightningBackend;
    private readonly ICashuBackend? cashuBackend;
    private readonly IIndexerBackend indexerBackend;
    private readonly ITelegramBotClient botClient;
    private readonly ILogger<PaymentMonitorService> logger;
    private readonly SessionWorkflowService workflowService;
    private readonly RecoveryService recoveryService;
}
