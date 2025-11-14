using teamZaps.Services;

namespace teamZaps.Sessions;

public class PaymentMonitorService : BackgroundService
{
    private readonly SessionManager _sessionManager;
    private readonly LnbitsService _lnbitsService;
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<PaymentMonitorService> _logger;
    private readonly SessionWorkflowService _workflowService;

    public PaymentMonitorService(SessionManager sessionManager, LnbitsService lnbitsService, ITelegramBotClient botClient, ILogger<PaymentMonitorService> logger, SessionWorkflowService workflowService)
    {
        _sessionManager = sessionManager;
        _lnbitsService = lnbitsService;
        _botClient = botClient;
        _logger = logger;
        _workflowService = workflowService;
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
                _logger.LogError(ex, "Error during payment monitoring loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CheckPendingPaymentsAsync(CancellationToken cancellationToken)
    {
        foreach (var session in _sessionManager.ActiveSessions)
        {
            foreach (var pending in session.PendingPayments.Values.ToList())
            {
                try
                {
                    var status = await _lnbitsService.CheckPaymentStatusAsync(pending.PaymentHash, cancellationToken).ConfigureAwait(false);
                    if (status is null)
                        continue;

                    if (status.Paid && !pending.NotifiedPaid)
                    {
                        pending.NotifiedPaid = true;
                        pending.PaidAt = DateTimeOffset.UtcNow;
                        // [Workaround] The `status.Amount` field doen't seem to be valid :(
                        pending.SettledSats = (long)pending.Amount;

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

                        var participant = _sessionManager.GetOrAddParticipant(session, pending.UserId, pending.DisplayName);
                        participant.Payments.Add(paymentRecord);
                        participant.TotalPaidSats += paymentRecord.AmountSats;

                        await MessageHelper.UpdatePinnedStatusAsync(session, _botClient, _workflowService, _logger, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error checking payment status for {PaymentHash}", pending.PaymentHash);
                }
            }
        }
    }
}
