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
        foreach (var winner in session.Winners)
        {
            try
            {
                await botClient.SendMessage(
                    winner.Key,
                    text: BuildSummary(session, winner.Key, winner.Value),
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken);
                    
                logger.LogDebug("Summary message sent to winner {WinnerId} for session {ChatId}", winner.Key, session.ChatId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send summary message to winner {WinnerId} for session {ChatId}", winner.Key, session.ChatId);
            }
        }
    }

    private static string BuildSummary(SessionState session, long winnerId, double fiatAmount)
    {
        Debug.Assert(session.Winners.Count > 0);
        Debug.Assert(session.HasPayments);

        var summary = new StringBuilder();
        summary.AppendLine("📋 *PAYMENT SUMMARY*\n");
        summary.AppendLine($"Session: *{session.ChatTitle}*\n");

        long winnerSats;
        if (session.Winners.Count > 1)
        {
            summary.AppendLine($"🎰 *Multiple winners selected!* You're one of {session.Winners.Count} winners.\n");
            summary.AppendLine($"Your share: 💰 *{fiatAmount:F2}€*");
            winnerSats = CalculateWinnerSats(session, fiatAmount);
        }
        else
        {
            summary.AppendLine($"🏆 You won to *pay fiat for sats*!");
            winnerSats = session.SatsAmount;
        }
        summary.AppendLine();

        summary.AppendLine("*Payments:*");
        foreach (var participant in session.Participants.Values)
        {
            summary.AppendLine($"*{participant}:*");
            summary.AppendPayments(participant.Payments);
        }
        summary.AppendLine();
        
        summary.AppendLine($"Total: 💶 {session.FormatTotalFiatAmount()}");
        summary.AppendLine();
        
        summary.AppendLine($"⚡ Please create a *lightning invoice* for *{winnerSats.Format()}* and send it to me now.");
        
        return (summary.ToString());
    }

    private static long CalculateWinnerSats(SessionState session, double fiatAmount)
    {
        var totalFiat = session.FiatAmount;
        var totalSats = session.SatsAmount;
        return (long)(totalSats * (fiatAmount / totalFiat));
    }
}
