using System.Diagnostics;
using teamZaps.Configuration;
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
            foreach (var pending in session.PendingPayments.Values.ToArray())
            {
                try
                {
                    var status = await lnbitsService.CheckPaymentStatusAsync(pending.PaymentHash, cancellationToken).ConfigureAwait(false);
                    if (status is null)
                        continue;
                    if (status.Paid && !pending.NotifiedPaid)
                    {
                        Debug.Assert(pending.FiatAmount == status.Details!.Extra!.FiatAmount);
                        Debug.Assert(pending.Currency == BotBehaviorOptions.AcceptedFiatCurrency);

                        pending.NotifiedPaid = true;
                        pending.PaidAt = DateTimeOffset.UtcNow;

                        // Update the payment message to show paid status
                        await PaymentMessage.UpdateAsync(pending, PaymentStatus.Paid, botClient, logger, cancellationToken);

                        var payment = new PaymentRecord()
                        {
                            UserId = pending.UserId,
                            DisplayName = pending.DisplayName,
                            PaymentHash = pending.PaymentHash,
                            PaymentRequest = pending.PaymentRequest,
                            Timestamp = pending.PaidAt ?? DateTimeOffset.UtcNow,
                            Tokens = pending.Tokens,
                            SatsAmount = status.Details!.Amount,
                            FiatAmount = pending.FiatAmount,
                            FiatRate = status.Details!.Extra!.FiatRate
                        };

                        session.PendingPayments.TryRemove(pending.PaymentHash, out _);

                        var participant = sessionManager.GetOrAddParticipant(session, pending.UserId, pending.DisplayName);
                        participant.Payments.Add(payment);

                        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);
                        
                        // Update user status messages for affected participant
                        await UserStatusMessage.UpdateAsync(session, participant.UserId, botClient, workflowService, logger, cancellationToken);
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
