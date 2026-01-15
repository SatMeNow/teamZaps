using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using TeamZaps.Backend;
using TeamZaps.Services;
using TeamZaps.Session;
using TeamZaps.Utils;

namespace TeamZaps.Statistic;

public class StatisticService : IHostedService
{
    #region Constants
    private static readonly TimeSpan HoldBackTime = TimeSpan.FromDays(365 * 5);
    #endregion


    public StatisticService(
        ILogger<StatisticService> logger,
        FileService<GeneralStatistics?> genStatsFile, FileService<GroupStatistics?> groupStatsFile, FileService<UserStatistics?> userStatsFile,
        SessionManager sessionManager,
        IIndexerBackend indexerBackend)
    {
        this.logger = logger;
        this.genStatsFile = genStatsFile;
        this.groupStatsFile = groupStatsFile;
        this.userStatsFile = userStatsFile;
        this.sessionManager = sessionManager;
        this.indexerBackend = indexerBackend;
    }


    #region Properties
    public GeneralStatistics? GeneralStats { get; set; } = null;
    public IReadOnlyDictionary<long, GroupStatistics> GroupStats => groupStats;
    private Dictionary<long, GroupStatistics> groupStats = new();
    public int Participants => userStats.Count;
    public IReadOnlyDictionary<long, UserStatistics> UserStats => userStats;
    private Dictionary<long, UserStatistics> userStats = new();
    public GroupRankingStatistics? GroupRanking { get; set; } = null;

    protected BlockHeader? CurrentBlock { get; private set; }
    protected int CurrentBlockHeight => (CurrentBlock?.Height ?? 0);
    #endregion


    #region Initialization
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Loading statistics.");
        try
        {
            this.userStats = (await userStatsFile.ReadAllAsync().ConfigureAwait(false))
                .Where(s => s is not null)
                .ToDictionary(s => s!.UserId, s => s!);
            this.groupStats = (await groupStatsFile.ReadAllAsync().ConfigureAwait(false))
                .Where(s => s is not null)
                .ToDictionary(s => s!.GroupId, s => s!);
            this.GeneralStats = await genStatsFile.ReadAsync().ConfigureAwait(false);

            CreateGroupRanking();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to load statistics", ex);
        }

        // [Testing]
        // await Examples.Sample_Statistics.CreateRandomStatistics(this, 50).ConfigureAwait(false);
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    #endregion
    #region Management
    public Task<bool> OnSessionCompleteAsync(SessionState session)
    {
        // Get current block:
        // > Assume last received block is the current block hence we invoke statistics only when session duration was determined.
        return (UpdateStatisticsAsync(session, indexerBackend.LastBlock!));
    }
    
    internal async Task<bool> UpdateStatisticsAsync(SessionState session, BlockHeader currentBlock)
    {
        Debug.Assert(currentBlock is not null);

        if (!session.IsValid)
        {
            logger.LogError("Cannot update statistics for invalid session {Session}.", session);
            return (false);
        }

        await update.WaitAsync().ConfigureAwait(false);
        try
        {
            this.CurrentBlock = currentBlock;

            // Update statistics:
            foreach (var userId in session.Participants.Keys)
                await UpdateUserStatisticsAsync(userId, session).ConfigureAwait(false);
            await UpdateGroupStatisticsAsync(session).ConfigureAwait(false);
            CreateSessionStatistics(session);
            await UpdateGeneralStatisticsAsync(session).ConfigureAwait(false);
            CreateGroupRanking();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update statistics for session {Session}.", session);
            return (false);
        }
        finally
        {
            update.Release();
        }
        return (true);
    }
    private async Task UpdateGeneralStatisticsAsync(SessionState session)
    {
        // Get or create new statistics:
        var stats = (GeneralStats ?? new GeneralStatistics());
        // Update metrics:
        stats.TotalParticipants = this.Participants;
        stats.TotalSessions++;
        stats.TotalGroups = GroupStats.Count;
        stats.Duration += (ulong)session.Duration!.Value;
        stats.StartedAtBlock ??= new MappedValue<BlockHeader>(session.ChatId, session.StartedAtBlock!);
        stats.TotalSats += (ulong)session.SatsAmount;
        stats.TotalTippedSats += (ulong)session.TipAmount;
        // Update parallel session metrics
        var parallelSessions = GetParallelSessions(session);
        stats.MaxParallelSessions = Math.Max(stats.MaxParallelSessions, parallelSessions.Count);
        stats.MaxParallelSats = Math.Max(stats.MaxParallelSats, parallelSessions.Sats);
        // Update per-month session metrics
        var month = GetMonth(session.CompletedAtBlock!.LocalTime);
        UpdateValue(stats.MaxParallelSessionsPerMonth, month, (v) => Math.Max(v, parallelSessions.Count));
        UpdateValue(stats.MaxParallelSatsPerMonth, month, (v) => Math.Max(v, parallelSessions.Sats));
        UpdateValue(stats.SessionsPerMonth, month, (v) => (v + 1));
        UpdateValue(stats.ParticipantsPerMonth, month, (v) => (v + session.Participants.Count));
        
        // Write updated statistics:
        GeneralStats = stats;
        await genStatsFile.WriteAsync(GeneralStats).ConfigureAwait(false);
    }
    private async Task UpdateGroupStatisticsAsync(SessionState session)
    {
        // Get or create new statistics:
        if (!groupStats.TryGetValue(session, out var stats))
            stats = new GroupStatistics() { GroupId = session, ChatTitle = session.ChatTitle! };
        // Update metrics:
        stats.ParticipantIds.UnionWith(session.Participants.Keys);
        stats.TotalSessions++;
        stats.Duration += (ulong)session.Duration!.Value;
        stats.StartedAtBlock ??= session.StartedAtBlock!;
        stats.TotalSats += (ulong)session.SatsAmount;
        stats.TotalTippedSats += (ulong)session.TipAmount;
        // Update per-month session metrics
        var month = GetMonth(session.CompletedAtBlock!.LocalTime);
        UpdateValue(stats.ParticipantsPerMonth, month, (v) => (v + session.Participants.Count));
        
        // Write updated statistics:
        groupStats[session] = stats;
        await groupStatsFile.WriteAsync(session, stats).ConfigureAwait(false);
    }
    private void CreateSessionStatistics(SessionState session)
    {
        session.Statistics = new()
        {
            GroupId = session.ChatId,
            StartBlock = session.StartedAtBlock!,
            EndBlock = session.CompletedAtBlock!,
            TotalSats = session.SatsAmount,
            TotalTippedSats = (long)session.TipAmount,
            ParticipantIds = new HashSet<long>(session.Participants.Keys)
        };
    }
    private async Task UpdateUserStatisticsAsync(long userId, SessionState session)
    {
        var participant = session.Participants[userId];

        // Get or create new statistics:
        if (!userStats.TryGetValue(userId, out var stats))
            stats = new UserStatistics() { UserId = userId };
        // Update metrics:
        stats.TotalSessions++;
        stats.Duration += (ulong)session.Duration!.Value;
        stats.StartedAtBlock ??= session.StartedAtBlock!;
        stats.TotalSats += (ulong)session.SatsAmount;
        stats.TotalTippedSats += (ulong)participant.Payments.Sum(p => p.TipAmount);
        // Update lottery metrics:
        if (session.LotteryParticipants.ContainsKey(userId))
        {
            stats.TotalLotteries++;
            if (session.Winners.TryGetValue(userId, out var winnerInfo))
            {
                stats.WonLotteries++;
                stats.TotalWonSats += (ulong)winnerInfo.SatsAmount;
            }
        }

        // Write updated statistics:
        userStats[userId] = stats;
        await userStatsFile.WriteAsync(userId, stats).ConfigureAwait(false);
    }
    private void CreateGroupRanking()
    {
        this.GroupRanking = null;
        if (GroupStats.Count < GroupRankingStatistics.MinGroups)
            logger.LogInformation("Skip creating group ranking hence we have not enough groups.");

        // Create new statistics:
        this.GroupRanking = new()
        {
            TotalSessions = GetRanking(s => s.TotalSessions),
            Duration = GetRanking(s => s.Duration),
            TotalSats = GetRanking(s => s.TotalSats),
            TotalTippedSats = GetRanking(s => s.TotalTippedSats),
            TotalTippedPercent = GetRanking(s => s.TotalTippedPercent),
            TippedSatsPerParticipant = GetRanking(s => (ulong)s.TippedSatsPerParticipant),
            SatsPerBlock = GetRanking(s => s.SatsPerBlock),
            SatsPerParticipant = GetRanking(s => s.SatsPerParticipant),
            SatsPerSession = GetRanking(s => s.SatsPerSession),
            ParticipantsLatestMonth = GetRanking(s => s.ParticipantsLatestMonth),
        };
    }
    #endregion


    #region Helper
    private void UpdateValue<T>(Dictionary<DateOnly, T> source, DateOnly month, Func<T, T> update)
    {
        if (source.TryGetValue(month, out var existing))
            source[month] = update(existing);
        else
            source[month] = update(default!);

        CleanupMonthly(source, CurrentBlock!);
    }
    private void CleanupMonthly<T>(Dictionary<DateOnly, T> source, BlockHeader referenceBlock)
    {
        var holdBack = DateOnly.FromDateTime(referenceBlock.LocalTime.DateTime.Subtract(HoldBackTime));
        var toRemove = source.Keys
            .Where(key => (key < holdBack))
            .ToArray();
        foreach (var key in toRemove)
            source.Remove(key);
    }
    private (int Count, long Sats) GetParallelSessions(SessionState session)
    {
        var parallelSessions = sessionManager.ActiveSessions
            .Prepend(session)
            .ToArray();
        return (parallelSessions.Length, parallelSessions.Sum(s => s.SatsAmount));
    }

    private MappedValue<T>[] GetRanking<T>(Func<GroupStatistics, T> selector) => GroupStats.Values
        .OrderByDescending(s => selector(s))
        .Take(GroupRankingStatistics.MaxGroups)
        .Select(s => new MappedValue<T>(s.GroupId, selector(s)))
        .ToArray();

    private DateOnly GetMonth(DateTimeOffset date) => new DateOnly(date.Year, date.Month, 1);
    private double GetMinutesSince(int startedHeight) => (CurrentBlockHeight - startedHeight).ToMinutes();
    private double GetHoursSince(int startedHeight) => (CurrentBlockHeight - startedHeight).ToHours();
    private double GetDaysSince(int startedHeight) => (CurrentBlockHeight - startedHeight).ToDays();
    private double GetMonthsSince(int startedHeight) => (CurrentBlockHeight - startedHeight).ToMonths();
    #endregion
    

    private readonly ILogger<StatisticService> logger;
    private readonly FileService<GeneralStatistics?> genStatsFile;
    private readonly FileService<GroupStatistics?> groupStatsFile;
    private readonly FileService<UserStatistics?> userStatsFile;

    private readonly SessionManager sessionManager;
    private readonly IIndexerBackend indexerBackend;
    
    private readonly SemaphoreSlim update = new SemaphoreSlim(1, 1);
}

#region Model.Statistics
public record MappedValue<T>(long GroupId, T Value)
{
    public bool Resolve(StatisticService statistics, [NotNullWhen(true)] out GroupStatistics? group) => statistics.GroupStats.TryGetValue(GroupId, out group);
}

[Storage("stats", "general.json")]
public record GeneralStatistics
{
    public uint TotalSessions { set; get; }
    public int TotalGroups { set; get; }

    /// <summary>
    /// Total duration of all sessions in blocks.
    /// </summary>
    public ulong Duration { set; get; }
    /// <summary>
    /// Block height of first session.
    /// </summary>
    public MappedValue<BlockHeader> StartedAtBlock { set; get; } = null!;

    /// <summary>
    /// Total amount of spent sats.
    /// </summary>
    public ulong TotalSats { set; get; }
    /// <summary>
    /// Total amount of tipped sats.
    /// </summary>
    public ulong TotalTippedSats { set; get; }
    /// <summary>
    /// Percentage of total sats that were tips.
    /// </summary>
    [JsonIgnore]
    public double TotalTippedPercent => (TotalSats > 0) ? (TotalTippedSats * 100 / (ulong)TotalSats) : 0;
    /// <summary>
    /// Average amount of tipped sats per participant.
    /// </summary>
    [JsonIgnore]
    public long TippedSatsPerParticipant => (TotalParticipants > 0) ? (long)(TotalTippedSats / (ulong)TotalParticipants) : 0;
    /// <summary>
    /// Average amount of sats per block.
    /// </summary>
    [JsonIgnore]
    public long SatsPerBlock => (Duration > 0) ? (long)(TotalSats / Duration) : 0;
    /// <summary>
    /// Average amount of sats per user.
    /// </summary>
    [JsonIgnore]
    public long SatsPerParticipant => (TotalParticipants > 0) ? (long)(TotalSats / (ulong)TotalParticipants) : 0;
    /// <summary>
    /// Average amount of sats per session.
    /// </summary>
    [JsonIgnore]
    public long SatsPerSession => (TotalSessions > 0) ? (long)(TotalSats / (ulong)TotalSessions) : 0;
    
    public Dictionary<DateOnly, int> SessionsPerMonth { get; set; } = new();
    /// <summary>
    /// Number of sessions in the last month.
    /// </summary>
    [JsonIgnore]
    public int SessionsLatestMonth => SessionsPerMonth.GetLatestValue();
    /// <summary>
    /// Maximum number of parallel sessions observed.
    /// </summary>
    public int MaxParallelSessions { set; get; }
    /// <summary>
    /// Maximum number of parallel sessions in a single month.
    /// </summary>
    public Dictionary<DateOnly, int> MaxParallelSessionsPerMonth { get; set; } = new();
    /// <summary>
    /// Maximum amount of sats in parallel sessions.
    /// </summary>
    public long MaxParallelSats { set; get; }
    /// <summary>
    /// Maximum amount of sats in parallel sessions within a month.
    /// </summary>
    public Dictionary<DateOnly, long> MaxParallelSatsPerMonth { get; set; } = new();
    public int TotalParticipants { set; get; }
    public Dictionary<DateOnly, int> ParticipantsPerMonth { get; set; } = new();
    /// <summary>
    /// Number of participants in the last month.
    /// </summary>
    [JsonIgnore]
    public int ParticipantsLatestMonth => ParticipantsPerMonth.GetLatestValue();
}

[Storage("stats/group", "group_{0}.json")]
public record GroupStatistics
{
    #region Constants
    public const int MinSessions = 3;
    #endregion


    public required long GroupId { init; get; }
    public required string ChatTitle { init; get; }

    public ulong TotalSessions { set; get; }

    /// <inheritdoc cref="GeneralStatistics.Duration"/> 
    public ulong Duration { set; get; }
    /// <inheritdoc cref="GeneralStatistics.StartedAtBlock"/> 
    public BlockHeader StartedAtBlock { set; get; } = null!;

    /// <inheritdoc cref="GeneralStatistics.TotalSats"/> 
    public ulong TotalSats { set; get; }
    /// <inheritdoc cref="GeneralStatistics.TotalTippedSats"/> 
    public ulong TotalTippedSats { set; get; }
    /// <inheritdoc cref="GeneralStatistics.TotalTippedPercent"/> 
    [JsonIgnore]
    public double TotalTippedPercent => (TotalSats > 0) ? (TotalTippedSats * 100 / TotalSats) : 0;
    /// <inheritdoc cref="GeneralStatistics.TippedSatsPerParticipant"/> 
    [JsonIgnore]
    public long TippedSatsPerParticipant => (TotalParticipants > 0) ? (long)(TotalTippedSats / (ulong)TotalParticipants) : 0;
    /// <inheritdoc cref="GeneralStatistics.SatsPerBlock"/> 
    [JsonIgnore]
    public long SatsPerBlock => (Duration > 0) ? (long)(TotalSats / Duration) : 0;
    /// <inheritdoc cref="GeneralStatistics.SatsPerUser"/> 
    [JsonIgnore]
    public long SatsPerParticipant => (TotalParticipants > 0) ? (long)(TotalSats / (ulong)TotalParticipants) : 0;
    /// <inheritdoc cref="GeneralStatistics.SatsPerSession"/> 
    [JsonIgnore]
    public long SatsPerSession => (TotalSessions > 0) ? (long)(TotalSats / (ulong)TotalSessions) : 0;
    
    [JsonIgnore]
    public int TotalParticipants => ParticipantIds.Count;
    public HashSet<long> ParticipantIds { get; } = new();
    public Dictionary<DateOnly, int> ParticipantsPerMonth { get; set; } = new();
    /// <inheritdoc cref="GeneralStatistics.ParticipantsLatestMonth"/> 
    [JsonIgnore]
    public int ParticipantsLatestMonth => ParticipantsPerMonth.GetLatestValue();
}

public record SessionStatistics
{
    public required long GroupId { init; get; }

    /// <inheritdoc cref="GeneralStatistics.Duration"/> 
    public int Duration => (EndBlock.Height - StartBlock.Height);
    public BlockHeader StartBlock { set; get; } = null!;
    public BlockHeader EndBlock { set; get; } = null!;

    /// <inheritdoc cref="GeneralStatistics.TotalSats"/> 
    public long TotalSats { set; get; }
    /// <inheritdoc cref="GeneralStatistics.TotalTippedSats"/> 
    public long TotalTippedSats { set; get; }
    /// <inheritdoc cref="GeneralStatistics.TotalTippedPercent"/> 
    [JsonIgnore]
    public double TotalTippedPercent => (TotalSats > 0) ? (TotalTippedSats * 100 / TotalSats) : 0;
    /// <inheritdoc cref="GeneralStatistics.TippedSatsPerParticipant"/> 
    [JsonIgnore]
    public long TippedSatsPerParticipant => (TotalParticipants > 0) ? (TotalTippedSats / TotalParticipants) : 0;
    /// <inheritdoc cref="GeneralStatistics.SatsPerBlock"/> 
    public long SatsPerBlock => (Duration > 0) ? (TotalSats / Duration) : 0;
    /// <inheritdoc cref="GeneralStatistics.SatsPerUser"/> 
    public long SatsPerParticipant => (TotalParticipants > 0) ? (TotalSats / TotalParticipants) : 0;
    
    public int TotalParticipants => ParticipantIds.Count;
    public HashSet<long> ParticipantIds { set; get; } = new();
}

[Storage("stats/user", "user_{0}.json")]
public record UserStatistics
{
    #region Constants
    public const int MinSessions = 3;
    #endregion


    public required long UserId { init; get; }

    public uint TotalSessions { set; get; }

    /// <inheritdoc cref="GeneralStatistics.Duration"/> 
    public ulong Duration { set; get; }
    /// <inheritdoc cref="GeneralStatistics.StartedAtBlock"/> 
    public BlockHeader StartedAtBlock { set; get; } = null!;

    /// <inheritdoc cref="GeneralStatistics.TotalSats"/> 
    public ulong TotalSats { set; get; }
    /// <inheritdoc cref="GeneralStatistics.TotalTippedSats"/> 
    public ulong TotalTippedSats { set; get; }
    /// <inheritdoc cref="GeneralStatistics.TotalTippedPercent"/> 
    [JsonIgnore]
    public double TotalTippedPercent => (TotalSats > 0) ? (TotalTippedSats * 100 / TotalSats) : 0;
    /// <inheritdoc cref="GeneralStatistics.SatsPerBlock"/> 
    [JsonIgnore]
    public long SatsPerBlock => (Duration > 0) ? (long)(TotalSats / Duration) : 0;
    /// <inheritdoc cref="GeneralStatistics.SatsPerSession"/> 
    [JsonIgnore]
    public long SatsPerSession => (TotalSessions > 0) ? (long)(TotalSats / TotalSessions) : 0;

    /// <summary>
    /// Total number of lotteries participated in.
    /// </summary>
    public uint TotalLotteries { set; get; }
    /// <summary>
    /// Number of lotteries won.
    /// </summary>
    public uint WonLotteries { set; get; }
    /// <summary>
    /// Number of lotteries lost (calculated).
    /// </summary>
    [JsonIgnore]
    public uint LostLotteries => (TotalLotteries - WonLotteries);
    /// <summary>
    /// Total amount of sats won from lotteries.
    /// </summary>
    public ulong TotalWonSats { set; get; }
    /// <summary>
    /// Average amount of won sats per session.
    /// </summary>
    [JsonIgnore]
    public long WonSatsPerSession => (TotalSessions > 0) ? (long)(TotalWonSats / TotalSessions) : 0;
}

public struct GroupRankingStatistics
{
    #region Constants
    public const int MinGroups = 3;
    public const int MaxGroups = 10;
    #endregion


    public MappedValue<ulong>[] TotalSessions { set; get; }

    /// <inheritdoc cref="GeneralStatistics.Duration"/> 
    public MappedValue<ulong>[] Duration { set; get; }

    /// <inheritdoc cref="GeneralStatistics.TotalSats"/> 
    public MappedValue<ulong>[] TotalSats { set; get; }
    /// <inheritdoc cref="GeneralStatistics.TotalTippedSats"/> 
    public MappedValue<ulong>[] TotalTippedSats { set; get; }
    /// <inheritdoc cref="GeneralStatistics.TotalTippedPercent"/> 
    public MappedValue<double>[] TotalTippedPercent { set; get; }
    /// <inheritdoc cref="GeneralStatistics.TippedSatsPerParticipant"/> 
    public MappedValue<ulong>[] TippedSatsPerParticipant { set; get; }
    /// <inheritdoc cref="GeneralStatistics.SatsPerBlock"/> 
    public MappedValue<long>[] SatsPerBlock { set; get; }
    /// <inheritdoc cref="GeneralStatistics.SatsPerUser"/> 
    public MappedValue<long>[] SatsPerParticipant { set; get; }
    /// <inheritdoc cref="GeneralStatistics.SatsPerSession"/> 
    public MappedValue<long>[] SatsPerSession { set; get; }
    public MappedValue<int>[] ParticipantsLatestMonth { set; get; }
}
#endregion


internal static partial class Ext
{
    public static bool TryGetRanking<T>(this MappedValue<T>[] source, long groupId, [NotNullWhen(true)] out int? rank, [NotNullWhen(true)] out T? value)
        where T : INumber<T>
    {
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i].GroupId == groupId)
            {
                value = source[i].Value;
                rank = (i + 1);
                return (true);
            }
        }
        value = default;
        rank = default;
        return (false);
    }
}