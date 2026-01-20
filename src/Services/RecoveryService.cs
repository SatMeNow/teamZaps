using System.Text;
using TeamZaps.Session;
using TeamZaps.Utils;
using TeamZaps.Configuration;
using TeamZaps.Handlers;

namespace TeamZaps.Services;

/// <summary>
/// Service for managing lost sats recovery system for interrupted sessions.
/// </summary>
public class RecoveryService : BackgroundService
{
    public RecoveryService(ILogger<RecoveryService> logger, FileService<LostSatsRecord> lostSatsFile, ITelegramBotClient botClient, IOptions<RecoverySettings> recoverySettings, SessionManager sessionManager)
    {
        this.logger = logger;
        this.lostSatsFile = lostSatsFile;
        this.botClient = botClient;
        this.recoverySettings = recoverySettings.Value;
        this.sessionManager = sessionManager;

        this.sessionManager.OnSessionRemoved += OnSessionRemoved;
    }


    #region Poperties.Management
    public bool Enabled => recoverySettings.Enable;
    public TimeSpan DailyScanTime => recoverySettings.DailyScanTime;
    #endregion


    #region Events
    private void OnSessionRemoved(object? sender, SessionState session)
    {
        // Clear recovery files for all participants in the removed session:
        ClearLostSats(session);
    }
    #endregion


    #region Initialization
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (recoverySettings.Enable == false)
            return;

        // Start daily scan loop:
        logger.LogInformation("Recovery service scheduled daily can at {DailyScanTime}.", DailyScanTime);

        var nextScanTime = GetNextScanTime(DateTimeOffset.Now);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = (nextScanTime - DateTimeOffset.Now);
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                await ScanForLostSatsAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during scheduled scan for lost sats.");
            }

            nextScanTime = GetNextScanTime(nextScanTime.AddDays(1));
        }
    }
    public override void Dispose()
    {
        this.sessionManager.OnSessionRemoved -= OnSessionRemoved;

        base.Dispose();
    }
    #endregion
    #region Management
    /// <summary>
    /// Records lost sats for a user (creates/updates recovery file).
    /// </summary>
    public async Task RecordLostSatsAsync(ParticipantState participant, string reason)
    {
        if (recoverySettings.Enable == false)
            return;

        try
        {
            var record = new LostSatsRecord
            {
                UserId = participant.UserId,
                UserName = participant.UserName(),
                SatsAmount = participant.SatsAmount,
                Timestamp = DateTimeOffset.Now,
                Reason = reason
            };
            await lostSatsFile.WriteAsync(record.UserId, record).ConfigureAwait(false);
            logger.LogDebug("Recorded lost sats for user {User}: {SatsAmount}", participant, record.SatsAmount.Format());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record lost sats for user {User}.", participant);
        }
    }
    public Task ClearLostSatsAsync(long userId)
    {
        if (recoverySettings.Enable == false)
            return (Task.CompletedTask);

        return (lostSatsFile.DeleteAsync(userId));
    }
    public void ClearLostSats(SessionState session)
    {
        if (recoverySettings.Enable == false)
            return;

        lostSatsFile.Delete(session.Participants.Values
            .Select(p => p.UserId));
    }

    public Task<LostSatsRecord?> TryGetLostSatsAsync(long userId)
    {
        if (recoverySettings.Enable == false)
            return (Task.FromResult<LostSatsRecord?>(null));

        return (lostSatsFile.ReadAsync(userId));
    }
    /// <summary>
    /// Gets all lost sats records.
    /// </summary>
    public Task<ICollection<LostSatsRecord>> GetAllLostSatsAsync()
    {
        if (recoverySettings.Enable == false)
            return (Task.FromResult<ICollection<LostSatsRecord>>(Array.Empty<LostSatsRecord>()));

        return (lostSatsFile.ReadAllAsync());
    }

    public async Task ScanForLostSatsAsync()
    {
        // Get lost sats of inactive(!) sessions/users:
        logger.LogDebug("Starting scan for lost sats.");
        var lostSatsRecords = (await GetAllLostSatsAsync().ConfigureAwait(false))
            .Where(r => !sessionManager.ActiveParticipants.Contains(p => p.UserId == r.UserId))
            .ToArray();
        if (lostSatsRecords.IsEmpty())
            return;

        var totalLostSats = lostSatsRecords.Sum(r => r.SatsAmount);
        logger.LogWarning("⚠️ LOST SATS DETECTED! Found {Count} user(s) with {TotalSats} of lost funds.", lostSatsRecords.Length, totalLostSats.Format());
        var exceededRecords = lostSatsRecords
            .Where(r => r.NotificationReminderLimitExceeded)
            .ToArray();
        if (!exceededRecords.IsEmpty())
            logger.LogWarning("{Count} user(s) aren't being notified about lost sats anymore.", exceededRecords.Length);

        // Notify users:
        foreach (var record in lostSatsRecords.Where(r => r.NotificationRequired))
        {
            try
            {
                // Send notification:
                var message = new StringBuilder()
                    .AppendRecoveryMessage(record)
                    .ToString();
                await botClient.SendMessage(record.UserId, message, 
                    parseMode: ParseMode.Markdown).ConfigureAwait(false);
                logger.LogDebug("Sent notification #{Count} about {SatsAmount} of lost funds to user {User}.",
                    record.NotifiedCount, record.SatsAmount.Format(), record.DisplayName());
                    
                // Update record:
                record.NotifiedCount++;
                record.LastNotified = DateTimeOffset.Now;
                await lostSatsFile.WriteAsync(record.UserId, record).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to notify user {User} about lost sats.", record.DisplayName());
            }
        }
    }
    #endregion
    #region Helper
    private DateTimeOffset GetNextScanTime(DateTimeOffset referenceTime)
    {
        var baseDate = new DateTimeOffset(referenceTime.Year, referenceTime.Month, referenceTime.Day, 0, 0, 0, referenceTime.Offset);
        var dailyScanTime = NormalizeTimeOfDay(DailyScanTime);
        var candidate = baseDate.Add(dailyScanTime);
        if (candidate < referenceTime)
            candidate = candidate.AddDays(1);
        return (candidate);
    }
    private static TimeSpan NormalizeTimeOfDay(TimeSpan value)
    {
        var day = TimeSpan.FromDays(1);
        var normalized = TimeSpan.FromTicks(value.Ticks % day.Ticks);
        if (normalized < TimeSpan.Zero)
            normalized += day;
        return (normalized);
    }
    #endregion


    private readonly ILogger<RecoveryService> logger;
    private readonly FileService<LostSatsRecord> lostSatsFile;
    private readonly ITelegramBotClient botClient;
    private readonly SessionManager sessionManager;
    private readonly RecoverySettings recoverySettings;
}


/// <summary>
/// Record of lost sats for a user.
/// </summary>
[Storage("lostSats", "user_{0}.json")]
public class LostSatsRecord : IUserName
{
    #region Constants.Settings
    static readonly TimeSpan MinRecoveryNotificationThreshold = TimeSpan.FromHours(12);
    private const int RecoveryNotificationMaxReminders = 3;
    #endregion


    #region Properties.Management
    [JsonIgnore]
    public bool NotificationReminderLimitExceeded => (NotifiedCount >= RecoveryNotificationMaxReminders);
    [JsonIgnore]
    public bool NotificationRequired
    {
        get
        {
            if (NotificationReminderLimitExceeded)
                return (false);
            if (LastNotified is null)
                return (true);
            if ((DateTimeOffset.Now - LastNotified.Value) >= MinRecoveryNotificationThreshold)
                return (true);
            return (false);
        }
    }
    #endregion
    #region Properties
    public required long UserId { get; set; }
    public required string UserName { get; set; } = string.Empty;

    public required long SatsAmount { get; set; }
    public required DateTimeOffset Timestamp { get; set; }
    public string? Reason { get; set; }

    public DateTimeOffset? LastNotified { get; set; } = null;
    public int NotifiedCount { get; set; } = 0;
    #endregion


    public override string ToString() => $"{this.DisplayName()}: {SatsAmount.Format()}";
}