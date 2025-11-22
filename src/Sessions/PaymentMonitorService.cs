using teamZaps.Services;

namespace teamZaps.Sessions;

public class PaymentMonitorService : BackgroundService
{
    public PaymentMonitorService(SessionManager sessionManager, LnbitsService lnbitsService, ITelegramBotClient botClient, ILogger<PaymentMonitorService> logger, SessionWorkflowService workflowService)
    {
        this.sessionManager = sessionManager;
        this.lnbitsService = lnbitsService;
        this.botClient = botClient;
        this.logger = logger;
        this.workflowService = workflowService;
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
                logger.LogError(ex, "Error during payment monitoring loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CheckPendingPaymentsAsync(CancellationToken cancellationToken)
    {
        foreach (var session in sessionManager.ActiveSessions)
        {
            foreach (var pending in session.PendingPayments.Values.ToList())
            {
                try
                {
                    var status = await lnbitsService.CheckPaymentStatusAsync(pending.PaymentHash, cancellationToken).ConfigureAwait(false);
                    if (status is null)
                        continue;

                    if (status.Paid && !pending.NotifiedPaid)
                    {
                        pending.NotifiedPaid = true;
                        pending.PaidAt = DateTimeOffset.UtcNow;
                        // [Workaround] The `status.Amount` field doen't seem to be valid :(
                        pending.SettledSats = (long)status.Details!.Amount;

                        // Update the payment message to show paid status
                        await PaymentMessage.UpdateAsync(pending, PaymentStatus.Paid, botClient, logger, cancellationToken);

                        var paymentRecord = new PaymentRecord(
                            pending.UserId,
                            pending.DisplayName,
                            pending.SettledSats ?? 0,
                            pending.Currency.ToString().ToUpperInvariant(),
                            pending.PaymentHash,
                            pending.PaymentRequest,
                            pending.InputExpression,
                            pending.PaidAt ?? DateTimeOffset.UtcNow);

                        session.PendingPayments.TryRemove(pending.PaymentHash, out _);
                        session.ConfirmedPayments.Add(paymentRecord);

                        var participant = sessionManager.GetOrAddParticipant(session, pending.UserId, pending.DisplayName);
                        participant.Payments.Add(paymentRecord);
                        participant.TotalPaidSats += paymentRecord.AmountSats;

                        await StatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error checking payment status for {PaymentHash}", pending.PaymentHash);
                }
            }
        }
    }


    private readonly SessionManager sessionManager;
    private readonly LnbitsService lnbitsService;
    private readonly ITelegramBotClient botClient;
    private readonly ILogger<PaymentMonitorService> logger;
    private readonly SessionWorkflowService workflowService;
}
