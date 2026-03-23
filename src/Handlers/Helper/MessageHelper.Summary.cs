using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using TeamZaps.Services;
using TeamZaps.Session;
using TeamZaps.Utils;
using Telegram.Bot.Types.ReplyMarkups;

namespace TeamZaps.Handlers;


/// <summary>
/// Summary message showing all payment tokens within a session to the winner.
/// </summary>
internal static class SessionSummaryMessage
{
    public static async Task SendAsync(ITelegramBotClient botClient, ILogger logger, SessionState session, bool cashuAvailable, CancellationToken cancellationToken)
    {
        foreach (var payout in session.WinnerPayouts)
        {
            if (payout.Value.PaymentCompleted)
                continue; // Already paid out via Cashu in DrawLotteryAsync

            try
            {
                ReplyMarkup? keyboard = cashuAvailable
                    ? new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData(
                        "🥜 Receive as Cashu token (no fee)", CallbackActions.PayoutViaCashu))
                    : null;

                await botClient.SendMessage(
                    payout.Key.UserId,
                    text: BuildSummary(session, payout.Value, cashuAvailable),
                    parseMode: ParseMode.Markdown,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                    
                logger.LogDebug("Summary message sent to winner {Winner} for session {Session}.", payout.Key, session);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send summary message to winner {Winner} for session {Session}.", payout.Key, session);
            }
        }
    }

    private static string BuildSummary(SessionState session, PayableFiatAmount winnerInfo, bool cashuAvailable)
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
        if (cashuAvailable)
        {
            summary.AppendLine($"*Your payout: {winnerSats.Format()}*");
            summary.AppendLine();
            summary.AppendLine($"🥜 Tap the button below for an instant *Cashu token* — no fee.");
            summary.AppendLine($"⚡ Or send me a *Lightning invoice* for exactly *{winnerSats.Format()}*.");
        }
        else
        {
            summary.AppendLine($"⚡ Please create a *lightning invoice* for *{winnerSats.Format()}* and send it to me now.");
            summary.AppendLine($"ℹ️ Feel free to split the payout into multiple invoices if needed.");
        }
        
        return (summary.ToString());
    }
}
