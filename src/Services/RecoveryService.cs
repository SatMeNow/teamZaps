using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Telegram.Bot;
using teamZaps.Helper;
using teamZaps.Sessions;
using teamZaps.Utils;
using teamZaps.Configuration;
using teamZaps.Backend;

namespace teamZaps.Services;

/// <summary>
/// Service for managing lost sats recovery system for interrupted sessions.
/// </summary>
public class RecoveryService : BackgroundService
{
    #region Constants.Settings
    static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ScanPeriod = TimeSpan.FromHours(6);
    #endregion
    #region Constants
    private const string RecoverFolder = "lostSats";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    #endregion


    public RecoveryService(ILogger<RecoveryService> logger, ITelegramBotClient botClient, IOptions<DebugSettings> debugSettings)
    {
        this.logger = logger;
        this.botClient = botClient;
        this.debugSettings = debugSettings.Value;

        this.RecoverDirectory = Path.Combine(AppContext.BaseDirectory, RecoverFolder);
    }


    #region Properties.Management
    string RecoverDirectory { get; }
    #endregion


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        #if DEBUG
        if (debugSettings.EnableRecovery == false)
            return;
        #endif

        // Ensure recovery directory exists:
        if (!Directory.Exists(RecoverDirectory))
        {
            Directory.CreateDirectory(RecoverDirectory);
            logger.LogInformation("Created recovery directory: {Directory}", RecoverDirectory);
        }

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
            await WriteRecordAsync(record).ConfigureAwait(false);

            //logger.LogInformation("Recorded lost sats for user {User}): {SatsAmount}", participant, participant.SatsAmount.Format());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record lost sats for user {User}", participant);
        }
    }
    /// <summary>
    /// Clears a lost sats records.
    /// </summary>
    public Task ClearLostSatsAsync(long userId) => DeleteRecordAsync(userId);
    /// <inheritdoc cref="ClearLostSatsAsync(SessionState)"/> 
    public void ClearLostSats(SessionState session)
    {
        _ = Task.Run(async () => ClearLostSatsAsync(session));
    }
    /// <summary>
    /// Clears all lost sats records of a session.
    /// </summary>
    public async Task ClearLostSatsAsync(SessionState session)
    {
        foreach (var participant in session.Participants.Values)
            await ClearLostSatsAsync(participant.UserId).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a lost sats records.
    /// </summary>
    public Task<LostSatsRecord?> TryGetLostSatsAsync(long userId) => ReadRecordAsync(GetRecoveryFilePath(userId));
    /// <summary>
    /// Gets all lost sats records.
    /// </summary>
    public async Task<ICollection<LostSatsRecord>> GetAllLostSatsAsync()
    {
        #if DEBUG
        if (debugSettings.EnableRecovery == false)
            return (Array.Empty<LostSatsRecord>());
        #endif

        var records = new List<LostSatsRecord>();
        try
        {
            if (!Directory.Exists(RecoverDirectory))
                return (records);

            // Process each recovery file:
            var files = Directory.GetFiles(RecoverDirectory, GetRecoveryFileName(null));
            foreach (var file in files)
            {
                var record = await ReadRecordAsync(file).ConfigureAwait(false);
                if (record is not null)
                    records.Add(record);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to scan recovery directory");
        }

        return (records);
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
                await WriteRecordAsync(record).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to notify user {User} about lost sats", record.DisplayName());
            }
        }
    }
    #endregion
    #region I/O
    private async Task WriteRecordAsync(LostSatsRecord record)
    {
        var filePath = GetRecoveryFilePath(record.UserId);
        var json = JsonSerializer.Serialize(record, JsonOptions);
        await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
    }
    private Task<LostSatsRecord?> ReadRecordAsync(long userId) => ReadRecordAsync(GetRecoveryFilePath(userId));
    private async Task<LostSatsRecord?> ReadRecordAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return (null);

            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            return (JsonSerializer.Deserialize<LostSatsRecord>(json, JsonOptions));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read lost sats from recovery file {File}", filePath);
            return (null);
        }
    }
    private async Task DeleteRecordAsync(long userId)
    {
        #if DEBUG
        if (debugSettings.EnableRecovery == false)
            return;
        #endif

        try
        {
            var filePath = GetRecoveryFilePath(userId);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                //logger.LogInformation("Deleted recovery file for user {UserId}", userId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete recovery file for user {UserId}", userId);
        }
    }
    #endregion


    #region Helper
    private string GetRecoveryFileName(long? userId) => $"user_{userId?.ToString() ?? "*"}.json";
    private string GetRecoveryFilePath(long? userId) => Path.Combine(RecoverDirectory, GetRecoveryFileName(userId));
    #endregion


    private readonly ILogger<RecoveryService> logger;
    private readonly ITelegramBotClient botClient;
    private readonly DebugSettings debugSettings;
}


/// <summary>
/// Record of lost sats for a user.
/// </summary>
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