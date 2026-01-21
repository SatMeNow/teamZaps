using TeamZaps.Backends;
using TeamZaps.Backends.Indexer;

namespace TeamZaps.Examples;

/// <summary>
/// Example usage of the ElectrumX service with subscription-based real-time updates.
/// </summary>
public class Sample_ElectrumX
{
    public static async Task RunExample(ElectrumXService electrumX)
    {
        try
        {
            #region Get current block (uses subscription cache)
            // Get current block header (includes height and time)
            // This will use cached data from subscription notifications if available
            Console.WriteLine("Getting current Bitcoin block header...");
            var header = await electrumX.GetCurrentBlockAsync();
            
            Console.WriteLine($"Current Block Height: {header.Height}");
            Console.WriteLine($"Current Block Hash: {header.Hash}");
            Console.WriteLine($"Current Block Time: {header.BlockTime}");
            Console.WriteLine($"Block Time (UTC): {header.BlockTime:yyyy-MM-dd HH:mm:ss}");

            // Calculate how long ago the block was mined
            var timeSinceBlock = DateTimeOffset.UtcNow - header.BlockTime;
            Console.WriteLine($"Time since last block: {timeSinceBlock.TotalMinutes:F1} minutes");
            Console.WriteLine();
            #endregion
            
            #region Service statistics
            Console.WriteLine($"ElectrumX service stats:");
            Console.WriteLine($"- Sent requests: {electrumX.SentRequests}");
            Console.WriteLine($"- Failed requests: {electrumX.FailedRequests}");
            Console.WriteLine($"- Active host: {electrumX.ActiveClient.Hostname}:{electrumX.ActiveClient.Port}");
            Console.WriteLine($"- Configured hosts: {electrumX.ConfiguredClients.Count}");
            Console.WriteLine();
            #endregion
            
            #region Real-time monitoring
            Console.WriteLine("Monitoring for new blocks (10 seconds)...");
            Console.WriteLine("Note: Block times average ~10 minutes, so you may not see a new block.");
            
            var startHeight = header.Height;
            var startTime = DateTime.UtcNow;
            
            while ((DateTime.UtcNow - startTime).TotalSeconds < 10)
            {
                await Task.Delay(2000);
                var currentHeader = await electrumX.GetCurrentBlockAsync();
                
                if (currentHeader.Height > startHeight)
                {
                    Console.WriteLine($"\n🎉 NEW BLOCK DETECTED! Height: {currentHeader.Height}");
                    if (currentHeader is BlockHeader newBlock)
                    {
                        Console.WriteLine($"   Hash: {newBlock.Hash}");
                        Console.WriteLine($"   Time: {newBlock.BlockTime:yyyy-MM-dd HH:mm:ss}");
                    }
                    startHeight = currentHeader.Height;
                }
                else
                {
                    Console.Write(".");
                }
            }
            
            Console.WriteLine($"\n\nMonitoring complete. Final height: {startHeight}");
            #endregion
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
