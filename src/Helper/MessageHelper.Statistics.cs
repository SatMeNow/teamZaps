using System.Text;
using teamZaps.Statistic;
using teamZaps.Session;
using Telegram.Bot.Types;
using teamZaps.Utils;
using System.Numerics;
using teamZaps.Backend;
using teamZaps.Services;
using System.Diagnostics;

namespace teamZaps.Helper;

/// <summary>
/// Server overview statistics message showing platform-wide metrics.
/// </summary>
internal static class ServerStatisticsMessage
{
    public static Task<Message> SendAsync(ITelegramBotClient botClient, StatisticService statisticService, long chatId, CancellationToken cancellationToken) => botClient.SendMessage(
        chatId: chatId,
        text: Build(statisticService),
        parseMode: ParseMode.Markdown,
        linkPreviewOptions: true,
        cancellationToken: cancellationToken);

    private static string Build(StatisticService statisticService)
    {
        var message = new StringBuilder();
        message.AppendLine("📊 *Platform statistics*");

        if (statisticService.GeneralStats is not null)
        {
            var stats = statisticService.GeneralStats;
            message.AppendLine();
            message.AppendLine($"*Overall activity*");
            message.AppendLine($"• Total sessions: {stats.TotalSessions}");
            message.AppendLatestMonth("Sessions", stats.SessionsPerMonth);
            message.AppendMappedValue("First session", stats.StartedAtBlock!, statisticService);
            message.AppendLine($"• Total duration: {stats.Duration} blocks");
            message.AppendLine($"• Total participants: {stats.TotalParticipants}");
            message.AppendLatestMonth("Participants", stats.ParticipantsPerMonth);
            message.AppendLine($"• Total sats: {stats.TotalSats.Format()}");
            message.AppendLine($"• Total tips: {stats.TotalTippedSats.Format()} ({stats.TotalTippedPercent:F2}%)");

            message.AppendLine();
            message.AppendLine($"*Liquidity metrics*");
            message.AppendLine($"• Avg sats/block: {stats.SatsPerBlock.Format()}");
            message.AppendLine($"• Avg sats/session: {stats.SatsPerSession.Format()}");
            message.AppendLine($"• Avg sats/participant: {stats.SatsPerParticipant.Format()}");
            message.AppendLine($"• Avg tips/participant: {stats.TippedSatsPerParticipant.Format()}");
            message.AppendLine($"• Max parallel sats: {stats.MaxParallelSats.Format()}");
            message.AppendLatestMonth("Parallel sats", (v) => v.Format()!, stats.MaxParallelSatsPerMonth);

            message.AppendLine();
            message.AppendLine($"*Performance metrics*");
            message.AppendLine($"• Max parallel sessions: {stats.MaxParallelSessions}");
            message.AppendLatestMonth("Parallel sessions", stats.MaxParallelSessionsPerMonth);
        }

        return message.ToString();
    }
}

/// <summary>
/// Group statistics message showing session and group metrics.
/// </summary>
internal static class GroupStatisticsMessage
{
    public static Task<Message?> SendIfAsync(ITelegramBotClient botClient, StatisticService statisticService, SessionState session, CancellationToken cancellationToken)
    {
        if (!statisticService.GroupStats.TryGetValue(session.ChatId, out var groupStats))
            return (Task.FromResult<Message?>(null));
        if (groupStats.TotalSessions < GroupStatistics.MinSessions)
            return (Task.FromResult<Message?>(null));

        return (SendAsync(botClient, statisticService, session.ChatId, session, cancellationToken)!);
    }
    public static Task<Message> SendAsync(ITelegramBotClient botClient, StatisticService statisticService, long chatId, CancellationToken cancellationToken) => SendAsync(botClient, statisticService, chatId, null, cancellationToken);
    private static Task<Message> SendAsync(ITelegramBotClient botClient, StatisticService statisticService, long chatId, SessionState? session, CancellationToken cancellationToken) => botClient.SendMessage(
        chatId: chatId,
        text: Build(chatId, session, statisticService),
        parseMode: ParseMode.Markdown,
        linkPreviewOptions: true,
        cancellationToken: cancellationToken);

    private static string Build(long chatId, SessionState? session, StatisticService statisticService)
    {
        var message = new StringBuilder();

        // Session-specific statistics
        var sessionStats = session?.Statistics;
        if (sessionStats is not null)
        {
            Debug.Assert(chatId == session!.ChatId);

            message.AppendLine($"📈 *Session statistics*");
            message.AppendLine();
            message.AppendLine($"• Duration: {sessionStats.Duration} blocks");
            //message.AppendLine($"• Block range: {sessionStats.StartBlock}..{sessionStats.EndBlock}");
            message.AppendLine($"• Participants: {sessionStats.TotalParticipants}");
            message.AppendLine($"• Total sats: {sessionStats.TotalSats.Format()}");
            message.AppendLine($"• Sats/block: {sessionStats.SatsPerBlock.Format()}");
            message.AppendLine($"• Sats/participant: {sessionStats.SatsPerParticipant.Format()}");
            message.AppendLine($"• Total tips: {sessionStats.TotalTippedSats.Format()} ({sessionStats.TotalTippedPercent:F2}%)");
            //message.AppendLine($"• Avg tips/participant: {sessionStats.TippedSatsPerParticipant.Format()}");
            message.AppendLine();
        }

        // Group statistics
        if (statisticService.GroupStats.TryGetValue(chatId, out var groupStats))
        {
            message.AppendLine("📊 *Group statistics*");
            message.AppendLine();
            message.AppendLine($"*Overall activity*");
            message.AppendLine($"• Sessions: {groupStats.TotalSessions}");
            message.AppendLine($"• First session: {groupStats.StartedAtBlock.Format()}");
            message.AppendLine($"• Total duration: {groupStats.Duration} blocks");
            message.AppendLine($"• Total participants: {groupStats.TotalParticipants}");
            message.AppendLatestMonth("Participants", groupStats.ParticipantsPerMonth);
            
            message.AppendLine();
            message.AppendLine($"*Liquidity metrics*");
            message.AppendLine($"• Total sats: {((long)groupStats.TotalSats).Format()}");
            message.AppendLine($"• Sats/block: {groupStats.SatsPerBlock.Format()}");
            message.AppendLine($"• Sats/session: {groupStats.SatsPerSession.Format()}");
            message.AppendLine($"• Sats/participant: {groupStats.SatsPerParticipant.Format()}");
            message.AppendLine($"• Total tips: {((long)groupStats.TotalTippedSats).Format()} ({groupStats.TotalTippedPercent:F2}%)");
            //message.AppendLine($"• Avg tips/participant: {groupStats.TippedSatsPerParticipant.Format()}");
        }

        if (message.Length == 0)
            throw new NullReferenceException("*No statistics 📊* yet.\nℹ️ There will be some available once this group completes a session.")
                .AddLogLevel(LogLevel.Warning);
                
        // Ranking statistics
        var ranking = statisticService.GroupRanking;
        if (ranking is not null)
        {
            var rb = new StringBuilder()
                .TryAppendRanking("Total sessions", ranking!.Value.TotalSessions, chatId)
                .TryAppendRanking("Total sats", ranking!.Value.TotalSats, chatId)
                .TryAppendRanking("Total tips", ranking!.Value.TotalTippedSats, chatId)
                .TryAppendRanking("Sats/session", ranking!.Value.SatsPerSession, chatId);
            if (rb.Length > 0)
            {
                message.AppendLine();
                message.AppendLine($"🏆 *Top {GroupRankingStatistics.MaxGroups} group rankings*");
                message.AppendLine();
                message.Append(rb);
            }
        }

        return message.ToString();
    }
}

/// <summary>
/// User statistics message showing individual user performance.
/// </summary>
internal static class UserStatisticsMessage
{
    public static Task<Message?> SendIfAsync(ITelegramBotClient botClient, StatisticService statisticService, User user, CancellationToken cancellationToken)
    {
        if (!statisticService.UserStats.TryGetValue(user.Id, out var userStats))
            return (Task.FromResult<Message?>(null));
        if (userStats.TotalSessions < UserStatistics.MinSessions)
            return (Task.FromResult<Message?>(null));

        return (SendAsync(botClient, statisticService, user, cancellationToken)!);
    }
    public static Task<Message> SendAsync(ITelegramBotClient botClient, StatisticService statisticService, User user, CancellationToken cancellationToken)
    {
        if (!statisticService.UserStats.TryGetValue(user.Id, out var userStats))
            throw new NullReferenceException("*No statistics 📊* yet.\nℹ️ There will be some available once you participated in a completed session.")
                .AddLogLevel(LogLevel.Warning);

        return (botClient.SendMessage(
            chatId: user.Id,
            text: Build(userStats),
            parseMode: ParseMode.Markdown,
            linkPreviewOptions: true,
            cancellationToken: cancellationToken));
    }
    private static string Build(UserStatistics statistic)
    {
        var message = new StringBuilder();
        message.AppendLine($"📊 *Personal statistics*");

        message.AppendLine();
        message.AppendLine($"*Overall*");
        message.AppendLine($"• Sessions joined: {statistic.TotalSessions}");
        message.AppendLine($"• First session: {statistic.StartedAtBlock.Format()}");
        message.AppendLine($"• Total duration: {statistic.Duration} blocks");
        message.AppendLine($"• Total sats: {((long)statistic.TotalSats).Format()}");
        message.AppendLine($"• Total tips: {((long)statistic.TotalTippedSats).Format()} ({statistic.TotalTippedPercent:F2}%)");
        
        message.AppendLine();
        message.AppendLine($"*Liquidity metrics*");
        message.AppendLine($"• Avg sats/session: {statistic.SatsPerSession.Format()}");
        message.AppendLine($"• Avg sats/block: {statistic.SatsPerBlock.Format()}");

        return message.ToString();
    }
}

/*
/// <summary>
/// Group rankings message showing top groups by various metrics.
/// </summary>
internal static class GroupRankingsMessage
{
    public static Task<Message> SendAsync(ITelegramBotClient botClient, StatisticService statisticService, long chatId, CancellationToken cancellationToken) => botClient.SendMessage(
        chatId: chatId,
        text: Build(statisticService),
        parseMode: ParseMode.Markdown,
        linkPreviewOptions: true,
        cancellationToken: cancellationToken);

    private static string Build(StatisticService statisticService)
    {
        var sb = new StringBuilder();
        sb.AppendLine("🏆 *Group rankings*");
        sb.AppendLine();

        if (statisticService.GroupRanking is null)
            return sb.AppendLine("No rankings available yet (minimum 3 groups required).").ToString();

        var ranking = statisticService.GroupRanking.Value;

        // Top by total sats
        if (ranking.TotalSats is not null && ranking.TotalSats.Length > 0)
        {
            sb.AppendLine("*By total sats*");
            for (int i = 0; i < Math.Min(5, ranking.TotalSats.Length); i++)
            {
                var entry = ranking.TotalSats[i];
                var groupName = statisticService.GroupStats.TryGetValue(entry.GroupId, out var g) ? g.ChatTitle : $"Group {entry.GroupId}";
                sb.AppendLine($"• {i + 1}. {groupName}: {((long)entry.Value).Format()}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
*/

internal static partial class Ext
{
    public static StringBuilder AppendLatestMonth<T>(this StringBuilder source, string label, Dictionary<DateOnly, T> dateItems)
        where T : INumber<T>
    {
        return (AppendLatestMonth(source, label, (v) => v, dateItems));
    }
    public static StringBuilder AppendLatestMonth<T>(this StringBuilder source, string label, Func<T, object> formatter, Dictionary<DateOnly, T> dateItems)
        where T : INumber<T>
    {
        var latest = dateItems.GetLatest();
        if (latest.Value > T.Zero)
            source.AppendLine($"• {label} in {latest.Key:Y}: {formatter(latest.Value)}"); // Oktober 2008
        return (source);
    }
    public static StringBuilder AppendMappedValue(this StringBuilder source, string label, MappedValue<BlockHeader> blockValue, StatisticService statistics)
    {
        if (blockValue.Resolve(statistics, out var group))
            source.AppendLine($"• {label}: {blockValue.Value.FormatHeight()}\n  by _{group.ChatTitle}_");
        return (source);
    }
    public static StringBuilder TryAppendRanking<T>(this StringBuilder source, string label, MappedValue<T>[] mappedValue, long groupId)
        where T : INumber<T>
    {
        if ((mappedValue.TryGetRanking(groupId, out var rank, out var value)) && (value > T.Zero))
        {
            var ranking = rankIcons.ElementAtOrDefault(rank.Value - 1);
            if (string.IsNullOrEmpty(ranking))
                ranking = $"#{rank.Value}";
            source.AppendLine($"• {ranking} {label}: {value}");
        }

        return (source);
    }
    static readonly string[] rankIcons = { "🥇", "🥈", "🥉" };
}