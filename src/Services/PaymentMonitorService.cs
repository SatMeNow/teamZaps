using System.Diagnostics;
using teamZaps.Configuration;
using teamZaps.Services;
using teamZaps.Services.Backends;

namespace teamZaps.Sessions;

public class PaymentMonitorService : BackgroundService
{
    public PaymentMonitorService(SessionManager sessionManager, ILightningBackend lightningBackend, ITelegramBotClient botClient, ILogger<PaymentMonitorService> logger, SessionWorkflowService workflowService, RecoveryService recoveryService)
    {
        this.sessionManager = sessionManager;
        this.lightningBackend = lightningBackend;
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
                    var status = await lightningBackend.CheckPaymentStatusAsync(pending.PaymentHash, cancellationToken).ConfigureAwait(false);
                    if (status is null)
                        continue;
                    if (status.Paid && !pending.NotifiedPaid)
                    {
                        #if DEBUG
                        var expectedAmount = ((ITipableAmount)pending).TotalFiatAmount;
                        var actualAmount = status!.FiatAmount;
                        var tolerance = Math.Max(0.01, expectedAmount * 0.01); // Allow 1% tolerance, minimum 1 cent
                        Debug.Assert(Math.Abs(expectedAmount - actualAmount) <= tolerance);
                        Debug.Assert(pending.Currency == BotBehaviorOptions.AcceptedFiatCurrency);
                        #endif

                        pending.NotifiedPaid = true;
                        pending.PaidAt = DateTimeOffset.UtcNow;

                        // Update the payment message to show paid status
                        await PaymentMessage.UpdateAsync(pending, PaymentStatus.Paid, botClient, logger, cancellationToken);

                        var payment = new PaymentRecord()
                        {
                            User = pending.User,
                            PaymentHash = pending.PaymentHash,
                            PaymentRequest = pending.PaymentRequest,
                            Timestamp = pending.PaidAt ?? DateTimeOffset.UtcNow,
                            Tokens = pending.Tokens,
                            SatsAmount = status!.SatsAmount,
                            FiatAmount = pending.FiatAmount,
                            TipAmount = pending.TipAmount,
                            FiatRate = status!.FiatRate
                        };

                        session.PendingPayments.TryRemove(pending.PaymentHash, out _);

                        var participant = sessionManager.GetOrAddParticipant(session, pending.User);
                        participant.Payments.Add(payment);

                        // Record lost sats for crash recovery:
                        await recoveryService.RecordLostSatsAsync(participant, $"Payment in session: *{session.ChatTitle}*");

                        await SessionStatusMessage.UpdateAsync(session, botClient, workflowService, logger, cancellationToken);
                        
                        // Update user status messages for affected participant
                        await UserStatusMessage.UpdateAsync(session, participant, botClient, workflowService, logger, cancellationToken);
                        
                        // Delete payment help message if it exists
                        if (participant.PaymentHelpMessageId is not null)
                        {
                            try
                            {
                                await botClient.DeleteMessage(participant.UserId, participant.PaymentHelpMessageId!.Value, cancellationToken);
                            }
                            catch (Exception)
                            {
                            }
                            
                            participant.PaymentHelpMessageId = null;
                        }
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
    private readonly ILightningBackend lightningBackend;
    private readonly ITelegramBotClient botClient;
    private readonly ILogger<PaymentMonitorService> logger;
    private readonly SessionWorkflowService workflowService;
    private readonly RecoveryService recoveryService;
}
