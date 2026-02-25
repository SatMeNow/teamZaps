using System.Diagnostics;
using TeamZaps.Session;
using TeamZaps.Utils;

namespace TeamZaps.Handlers;

internal static class LotteryHelper
{
    public static int SelectWinners(SessionState session)
    {
        Debug.Assert(session.WinnerPayouts.IsEmpty());

        var remainingAmount = ((ITipableAmount)session).TotalFiatAmount;

        // Shuffle participants for fair random selection:
        var seed = HashCode.Combine(Environment.TickCount, session.ChatTitle, session.StartedAtBlock?.Height, session.Participants.Count, remainingAmount);
        var random = new Random(seed);
        var lotteryParticipants = session.LotteryParticipants
            .OrderBy(_ => random.Next())
            .ToArray();

        foreach (var participant in lotteryParticipants)
        {
            if (remainingAmount <= 0)
                break;

            var budget = participant.Value;
            var amountToPay = Math.Min(budget, remainingAmount);
            var satsAmount = CalculateWinnerSats(session, amountToPay);

            session.WinnerPayouts[participant.Key] = new PayableFiatAmount(amountToPay, satsAmount);

            remainingAmount -= amountToPay;
        }

        return (session.WinnerPayouts.Count);
    }

    public static long CalculateWinnerSats(SessionState session, double fiatAmount)
    {
        // Don't use any exchange rate here!
        // > Just calculate proportionally to the total fiat and sats amounts:
        var totalFiat = ((ITipableAmount)session).TotalFiatAmount;
        var totalSats = session.SatsAmount;
        return (long)(totalSats * (fiatAmount / totalFiat));
    }
}
