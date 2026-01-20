using System;
using TeamZaps.Session;
using TeamZaps.Backends;
using TeamZaps.Payment;
using TeamZaps.Services;

namespace TeamZaps.Examples;

/// <summary>
/// Sample demonstrating how to invoke StatisticService with randomly generated sessions.
/// </summary>
public class Sample_Statistics
{
    public static async Task CreateRandomStatistics(StatisticService statisticService, int sessions = 10)
    {
        // Generate random sessions
        var randomSessions = GenerateRandomSessions(sessions);

        // Update statistics for each session
        Console.WriteLine("=== Updating Statistics ===");
        foreach (var session in randomSessions)
        {
            var success = await statisticService.UpdateStatisticsAsync(session, session.CompletedAtBlock!);
            Console.WriteLine($"Session {session.ChatId} - {session.ChatTitle}: {(success ? "✓ Updated" : "✗ Failed")}");
        }

        // Display aggregated statistics
        Console.WriteLine("\n=== Statistics Summary ===");
        if (statisticService.GeneralStats != null)
        {
            Console.WriteLine($"Total Sessions: {statisticService.GeneralStats.TotalSessions}");
            Console.WriteLine($"Total Users: {statisticService.GeneralStats.TotalParticipants}");
            Console.WriteLine($"Total Sats: {statisticService.GeneralStats.TotalSats}");
            Console.WriteLine($"Total Tipped Sats: {statisticService.GeneralStats.TotalTippedSats}");
            Console.WriteLine($"Tipped Percentage: {statisticService.GeneralStats.TotalTippedPercent:F2}%");
            Console.WriteLine($"Sessions latest Month: {statisticService.GeneralStats.SessionsLatestMonth:F2}");
            Console.WriteLine($"Participants latest Month: {statisticService.GeneralStats.ParticipantsLatestMonth:F2}");
            Console.WriteLine($"Sats per Session: {statisticService.GeneralStats.SatsPerSession}");
            Console.WriteLine($"Tipped Sats per Participant: {statisticService.GeneralStats.TippedSatsPerParticipant}");
        }

        Console.WriteLine($"\nTotal Groups: {statisticService.GroupStats.Count}");
        Console.WriteLine($"Total Users (across groups): {statisticService.UserStats.Count}");

        if (statisticService.GroupRanking?.TotalSats != null)
        {
            const int topN = 5;
            Console.WriteLine($"\n=== Top {topN} Groups by Total Sats ===");
            foreach (var ranked in statisticService.GroupRanking!.Value.TotalSats.Take(topN))
                Console.WriteLine($"  Group {ranked.GroupId}: {ranked.Value} sats");
        }

        if (statisticService.GroupRanking?.TotalTippedSats != null)
        {
            const int topN = 5;
            Console.WriteLine($"\n=== Top {topN} Groups by Total Tipped Sats ===");
            foreach (var ranked in statisticService.GroupRanking!.Value.TotalTippedSats.Take(topN))
                Console.WriteLine($"  Group {ranked.GroupId}: {ranked.Value} sats tipped");
        }
    }

    /// <summary>
    /// Generates random session data for testing with realistic distributions.
    /// </summary>
    private static List<SessionState> GenerateRandomSessions(int count)
    {
        var random = new Random();
        var sessions = new List<SessionState>();
        var groups = new Dictionary<long, string>
        {
            { 123456789, "Bitcoin Dev Group" },
            { 987654321, "Lightning Network Community" },
            { 555555555, "Zaps Discussion" },
            { 777777777, "Bitcoin Trading" },
            { 999999999, "General Crypto" }
        };
        var startBlock = random.Next(900000, 901000);
        var baseFiatPrice = 0.00003; // BTC/USD rate approximation

        for (int i = 0; i < count; i++)
        {
            var chatId = groups.Keys.ElementAt(random.Next(groups.Count));
            var userId = (1000000 + random.Next(10000));
            var numParticipants = random.Next(3, 12); // More realistic participant count

            var session = new SessionState
            {
                ChatId = chatId,
                ChatTitle = groups[chatId],
                StartedByUser = new ParticipantState(new User
                {
                    Id = userId,
                    IsBot = false,
                    FirstName = $"User{userId}"
                }),
                StartedAtBlock = CreateBlockHeader(startBlock),
                CompletedAtBlock = CreateBlockHeader(startBlock + random.Next(6, 36))
            };

            // Add random participants with payments
            for (int j = 0; j < numParticipants; j++)
            {
                var participantId = 2000000 + random.Next(100000);
                var participant = new ParticipantState(new User
                {
                    Id = participantId,
                    IsBot = false,
                    FirstName = $"Participant{participantId}"
                });

                // Generate realistic payment amounts with log-normal distribution
                var satsAmount = (long)(random.Next(500, 50000));
                
                // Tips: 10-30% of payment amount (more realistic for zap culture)
                var tipPercentage = 0.10 + (random.NextDouble() * 0.20);
                var tipAmount = satsAmount * tipPercentage;

                participant.Payments.Add(new PaymentRecord
                {
                    User = participant.User,
                    PaymentHash = Guid.NewGuid().ToString("N").Substring(0, 32),
                    PaymentRequest = $"lnbc{satsAmount}n...",
                    Timestamp = DateTimeOffset.UtcNow.AddHours(-random.Next(0, 24)),
                    Tokens = Array.Empty<PaymentToken>(),
                    SatsAmount = satsAmount,
                    TipAmount = tipAmount,
                    FiatAmount = satsAmount * baseFiatPrice
                });

                session.Participants.TryAdd(participantId, participant);
            }

            sessions.Add(session);

            switch (i % 4)
            {
                // Move to very close block (for parallel sessions):
                case 0: startBlock += random.Next(0, 3); break;
                // Move to next block within a month:
                case 1: startBlock += random.Next((int)1.WeeksToBlocks(), (int)1.MonthsToBlocks()); break;

                // Move to next block within a few days:
                default: startBlock += random.Next((int)1.DaysToBlocks(), (int)3.DaysToBlocks()); break;
            }
        }

        return sessions;
    }

    private static BlockHeader CreateBlockHeader(int height)
    {
        const int BlockTimeMinutes = 10;
        var genesisTime = new DateTimeOffset(2009, 1, 3, 19, 15, 5, TimeSpan.Zero);
        
        return new BlockHeader
        {
            Height = height,
            Hash = height.ToString().PadLeft(64, '0'),
            BlockTime = genesisTime.AddMinutes(height * BlockTimeMinutes)
        };
    }
}
