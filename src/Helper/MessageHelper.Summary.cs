using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using teamZaps.Configuration;
using teamZaps.Services;
using teamZaps.Utils;

namespace teamZaps.Sessions;


/// <summary>
/// Summary message showing all payment tokens to the winner.
/// </summary>
internal static class SummaryMessage
{
    public static async Task SendAsync(SessionState session, ITelegramBotClient botClient, Microsoft.Extensions.Logging.ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var summaryText = BuildSummary(session);
            await botClient.SendMessage(
                session.WinnerUserId!,
                summaryText,
                ParseMode.Markdown,
                cancellationToken: cancellationToken);
                
            logger.LogDebug("Summary message sent to winner {WinnerUserId} for session {ChatId}", session.WinnerUserId!, session.ChatId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send summary message to winner {WinnerUserId} for session {ChatId}", session.WinnerUserId!, session.ChatId);
        }
    }

    private static string BuildSummary(SessionState session)
    {
        Debug.Assert(session.WinnerUserId is not null);
        Debug.Assert(session.HasPayments);

        var summary = new StringBuilder();
        summary.AppendLine("📋 *PAYMENT SUMMARY*\n");
        summary.AppendLine($"Session: *{session.ChatTitle}*\n");
        summary.AppendLine("*Payments:*");
        foreach (var participant in session.Participants.Values)
        {
            summary.AppendLine($"\n*{participant.DisplayName}:*");
            foreach (var token in participant.Payments.SelectMany(p => p.Tokens))
            {
                var memo = string.IsNullOrWhiteSpace(token.Note) ? "" : $" - {token.Note}";
                summary.AppendLine($"  • {token.FormatAmount()}{memo}");
            }
        }
        summary.AppendLine();
        summary.AppendLine($"*Total:* {session.FormatAmount()}");
        
        return summary.ToString();
    }
}
