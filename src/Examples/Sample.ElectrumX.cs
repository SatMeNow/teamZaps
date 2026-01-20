using TeamZaps.Backends;
using TeamZaps.Backends.Indexer;

namespace TeamZaps.Examples;

/// <summary>
/// Example usage of the ElectrumX service to get current block time.
/// </summary>
public class Sample_ElectrumX
{
    public static async Task RunExample(ElectrumXService electrumX)
    {
        try
        {
            #region Common backend tasks
            // Get current block header (includes height and time)
            Console.WriteLine("Getting current Bitcoin block header...");
            var header = await electrumX.GetCurrentBlockAsync();
            
            Console.WriteLine($"Current Block Height: {header.Height}");
            if (header is BlockHeader blockHeader)
            {
                Console.WriteLine($"Current Block Time: {blockHeader.BlockTime}");
                Console.WriteLine($"Block Time (UTC): {blockHeader.BlockTime:yyyy-MM-dd HH:mm:ss}");

                // Calculate how long ago the block was mined
                var timeSinceBlock = DateTimeOffset.UtcNow - blockHeader.BlockTime;
                Console.WriteLine($"Time since last block: {timeSinceBlock.TotalMinutes:F1} minutes");
            }

            // Get a specific block header (e.g., block 100,000)
            Console.WriteLine("\nGetting block 100,000...");
            var historicHeader = await electrumX.GetBlockAsync(100_000);
            Console.WriteLine($"Block 100,000 header: {historicHeader[..40]}...");
            #endregion

            #region Specific host tasks
            var someHost = electrumX.Hosts.First();
            
            // Get server features
            Console.WriteLine("\nGetting server features...");
            var features = await someHost.GetServerFeaturesAsync();
            Console.WriteLine($"Server version: {features.ServerVersion}");
            Console.WriteLine($"Protocol version: {features.ProtocolMin} - {features.ProtocolMax}");
            Console.WriteLine($"Genesis hash: {features.GenesisHash}");
            #endregion
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
