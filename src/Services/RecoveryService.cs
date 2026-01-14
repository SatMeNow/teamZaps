using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Telegram.Bot;
using TeamZaps.Helper;
using TeamZaps.Session;
using TeamZaps.Utils;
using TeamZaps.Configuration;
using TeamZaps.Backend;

namespace TeamZaps.Services;

/// <summary>
/// Service for managing lost sats recovery system for interrupted sessions.
/// </summary>
public class RecoveryService : BackgroundService
{
    #region Constants.Settings
    static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ScanPeriod = TimeSpan.FromHours(6);
    #endregion


    public RecoveryService(ILogger<RecoveryService> logger, FileService<LostSatsRecord> lostSatsFile, ITelegramBotClient botClient, IOptions<DebugSettings> debugSettings)
    {
        this.logger = logger;
        this.lostSatsFile = lostSatsFile;
        this.botClient = botClient;
        this.debugSettings = debugSettings.Value;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        #if DEBUG
        if (debugSettings.EnableRecovery == false)
            return;
        #endif

        await Task.Delay(InitTimeout).ConfigureAwait(false);

        // Schedule periodic scans:
        logger.LogInformation("Recovery service starting periodic scans");
        using var timer = new PeriodicTimer(ScanPeriod);
        do
        {
            try
            {
                await ScanForLostSatsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during periodic scan for lost sats.");
            }
            
        } while ((!stoppingToken.IsCancellationRequested) && (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)));
    }


    #region Management
    /// <summary>
    /// Records lost sats for a user (creates/updates recovery file).
    /// </summary>
    public async Task RecordLostSatsAsync(ParticipantState participant, string reason)
    {
        #if DEBUG
        if (debugSettings.EnableRecovery == false)
            return;
        #endif

        try
        {
            // Create recovery record:
            var record = new LostSatsRecord
            {
                UserId = participant.UserId,
                UserName = participant.UserName(),
                SatsAmount = participant.SatsAmount,
                Timestamp = DateTimeOffset.Now,
                Reason = reason,
                LastNotified = null // Reset notification timestamp for new payment
            };
            await lostSatsFile.WriteAsync(record.UserId, record).ConfigureAwait(false);

            //logger.LogInformation("Recorded lost sats for user {User}): {SatsAmount}", participant, participant.SatsAmount.Format());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record lost sats for user {User}", participant);
        }
    }
    public Task ClearLostSatsAsync(long userId)
    {
        #if DEBUG
        if (debugSettings.EnableRecovery == false)
            return (Task.CompletedTask);
        #endif

        return (lostSatsFile.DeleteAsync(userId));
    }
    public void ClearLostSats(SessionState session)
    {
        #if DEBUG
        if (debugSettings.EnableRecovery == false)
            return;
        #endif

        lostSatsFile.Delete(session.Participants.Values
            .Select(p => p.UserId));
    }

    public Task<LostSatsRecord?> TryGetLostSatsAsync(long userId)
    {
        #if DEBUG
        if (debugSettings.EnableRecovery == false)
            return (Task.FromResult<LostSatsRecord?>(null));
        #endif

        return (lostSatsFile.ReadAsync(userId));
    }
    /// <summary>
    /// Gets all lost sats records.
    /// </summary>
    public Task<ICollection<LostSatsRecord>> GetAllLostSatsAsync()
    {
        #if DEBUG
        if (debugSettings.EnableRecovery == false)
            return (Task.FromResult<ICollection<LostSatsRecord>>(Array.Empty<LostSatsRecord>()));
        #endif

        return (lostSatsFile.ReadAllAsync());
    }

    public async Task ScanForLostSatsAsync()
    {
        #if DEBUG
        if (debugSettings.EnableRecovery == false)
            return;
        #endif

        var lostSatsRecords = await GetAllLostSatsAsync().ConfigureAwait(false);
        if (lostSatsRecords.IsEmpty())
            return;

        var totalLostSats = lostSatsRecords.Sum(r => r.SatsAmount);
        logger.LogWarning("⚠️ LOST SATS DETECTED! Found {Count} user(s) with {TotalSats} of lost funds.", lostSatsRecords.Count, totalLostSats.Format());

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
                    
                logger.LogInformation("Notified user {User} about {SatsAmount} of lost funds", record.DisplayName(), record.SatsAmount.Format());
                
                // Update the record with notification timestamp:
                record.LastNotified = DateTimeOffset.Now;
                await lostSatsFile.WriteAsync(record.UserId, record).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to notify user {User} about lost sats", record.DisplayName());
            }
        }
    }
    #endregion


    private readonly ILogger<RecoveryService> logger;
    private readonly FileService<LostSatsRecord> lostSatsFile;
    private readonly ITelegramBotClient botClient;
    private readonly DebugSettings debugSettings;
}


/// <summary>
/// Record of lost sats for a user.
/// </summary>
[Storage("lostSats", "user_{0}.json")]
public class LostSatsRecord : IUserName
{
    #region Constants.Settings
    static readonly TimeSpan RecoveryNotificationPeriod = TimeSpan.FromDays(7);
    #endregion


    #region Properties.Management
    [JsonIgnore]
    public bool NotificationRequired => (LastNotified is null) || ((DateTimeOffset.Now - LastNotified.Value) < RecoveryNotificationPeriod);
    #endregion
    #region Properties
    public required long UserId { get; set; }
    public required string UserName { get; set; } = string.Empty;
    public required long SatsAmount { get; set; }
    public required DateTimeOffset Timestamp { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? LastNotified { get; set; }
    #endregion


    public override string ToString() => $"{this.DisplayName()}: {SatsAmount.Format()}";
}