using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using TeamZaps.Services;
using TeamZaps.Session;
using TeamZaps.Utils;

namespace TeamZaps.Handlers;


/// <summary>
/// Summary message showing all payment tokens within a session to the winner.
/// </summary>
internal static class SessionSummaryMessage
{
    public static async Task SendAsync(ITelegramBotClient botClient, ILogger logger, SessionState session, CancellationToken cancellationToken)
    {
        foreach (var payout in session.WinnerPayouts)
        {
            try
            {
                await botClient.SendMessage(
                    payout.Key.UserId,
                    text: BuildSummary(session, payout.Value),
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                    
                logger.LogDebug("Summary message sent to winner {Winner} for session {Session}.", payout.Key, session);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send summary message to winner {Winner} for session {Session}.", payout.Key, session);
            }
        }
    }

    private static string BuildSummary(SessionState session, PayableFiatAmount winnerInfo)
    {
        Debug.Assert(session.WinnerPayouts.Count > 0);
        Debug.Assert(session.HasPayments);

        var summary = new StringBuilder();
        summary.AppendLine("📋 *PAYMENT SUMMARY*\n");
        summary.AppendLine($"Session: *{session.DisplayTitle}*\n");

        long winnerSats = winnerInfo.SatsAmount;
        if (session.WinnerPayouts.Count > 1)
        {
            summary.AppendLine($"🎰 *Multiple winners selected!* You're one of {session.WinnerPayouts.Count} winners.\n");
            summary.AppendLine($"Your share: 💰 *{winnerInfo.FiatAmount.Format()}*");
        }
        else
        {
            summary.AppendLine($"🏆 You won to *pay fiat for sats*!");
        }
        summary.AppendLine();

        summary.AppendLine("*Orders* in this session:");
        foreach (var participant in session.Participants.Values)
        {
            summary.AppendLine($"{participant.MarkdownDisplayName()}:");
            summary.AppendOrders(participant.Orders);
        }
        summary.AppendLine();
        
        summary.AppendLine($"Total: 💶 {session.FormatOrderedAmount()}");
        summary.AppendLine();
        summary.AppendLine($"⚡ Please create a *lightning invoice* for *{winnerSats.Format()}* and send it to me now.");
        summary.AppendLine($"ℹ️ Feel free to split the payout into multiple invoices if needed.");
        
        return (summary.ToString());
    }
}
