using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using teamZaps.Configuration;
using teamZaps.Services;
using teamZaps.Utils;

namespace teamZaps.Sessions;


/// <summary>
/// Summary message showing all payment tokens within a session to the winner.
/// </summary>
internal static class SessionSummaryMessage
{
    public static async Task SendAsync(SessionState session, ITelegramBotClient botClient, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
    {
        foreach (var winnerUser in session.WinnerUsers)
        {
            var winnerInfo = session.Winners[winnerUser.UserId];
            try
            {
                await botClient.SendMessage(
                    winnerUser.UserId,
                    text: BuildSummary(session, winnerUser.UserId, winnerInfo),
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                    
                logger.LogDebug("Summary message sent to winner {Winner} for session {Session}", winnerUser, session);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send summary message to winner {Winner} for session {Session}", winnerUser, session);
            }
        }
    }

    private static string BuildSummary(SessionState session, long winnerId, WinnerInfo winnerInfo)
    {
        Debug.Assert(session.Winners.Count > 0);
        Debug.Assert(session.HasPayments);

        var summary = new StringBuilder();
        summary.AppendLine("📋 *PAYMENT SUMMARY*\n");
        summary.AppendLine($"Session: *{session.ChatTitle}*\n");

        long winnerSats = winnerInfo.SatsAmount;
        if (session.Winners.Count > 1)
        {
            summary.AppendLine($"🎰 *Multiple winners selected!* You're one of {session.Winners.Count} winners.\n");
            summary.AppendLine($"Your share: 💰 *{winnerInfo.FiatAmount.Format()}*");
        }
        else
        {
            summary.AppendLine($"🏆 You won to *pay fiat for sats*!");
        }
        summary.AppendLine();

        summary.AppendLine("*Payments:*");
        foreach (var participant in session.Participants.Values)
        {
            summary.AppendLine($"{participant.MarkdownDisplayName()}:");
            summary.AppendPayments(participant.Payments);
        }
        summary.AppendLine();
        
        summary.AppendLine($"Total: 💶 {session.FormatTotalFiatAmount()}");
        summary.AppendLine();
        
        summary.AppendLine($"⚡ Please create a *lightning invoice* for *{winnerSats.Format()}* and send it to me now.");
        
        return (summary.ToString());
    }
}
