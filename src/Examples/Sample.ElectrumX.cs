using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using teamZaps.Backend;
using teamZaps.Configuration;

namespace teamZaps.Examples;

/// <summary>
/// Example usage of the ElectrumX service to get current block time.
/// </summary>
public class Sample_ElectrumX
{
    public static async Task RunExample(ElectrumXService electrumX)
    {
        try
        {
            // Get current block header (includes height and time)
            Console.WriteLine("Getting current Bitcoin block header...");
            var header = await electrumX.GetCurrentBlockAsync();
            
            Console.WriteLine($"Current Block Height: {header.Height}");
            Console.WriteLine($"Current Block Time: {header.BlockTime}");
            Console.WriteLine($"Block Time (UTC): {header.BlockTime:yyyy-MM-dd HH:mm:ss}");

            // Calculate how long ago the block was mined
            var timeSinceBlock = DateTimeOffset.UtcNow - header.BlockTime;
            Console.WriteLine($"Time since last block: {timeSinceBlock.TotalMinutes:F1} minutes");

            // Get server features
            Console.WriteLine("\nGetting server features...");
            var features = await electrumX.GetServerFeaturesAsync();
            Console.WriteLine($"Server version: {features?["server_version"]}");
            Console.WriteLine($"Protocol version: {features?["protocol_min"]} - {features?["protocol_max"]}");
            Console.WriteLine($"Genesis hash: {features?["genesis_hash"]}");

            // Get a specific block header (e.g., block 100,000)
            Console.WriteLine("\nGetting block 100,000...");
            var historicHeader = await electrumX.GetBlockHeaderAsync(100_000);
            Console.WriteLine($"Block 100,000 header: {historicHeader[..40]}...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
