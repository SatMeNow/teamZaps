using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using TeamZaps.Backends.Indexer;
using TeamZaps.Configuration;

namespace TeamZaps.Tests.Backend;


public abstract class ElectrumXTests : BackendUnitTest<ElectrumXService>
{
    #region Log
    protected static void LogTest(string message) => Debug.WriteLine(">>>> " + message);
    #endregion
}
public abstract class CommunicativeElectrumXTests : ElectrumXTests, IDisposable
{
    public CommunicativeElectrumXTests()
    {
        // Create settings:
        var settings = Options.Create(new ElectrumXSettings
        {
            Port = 50002,
            Hosts = new[]
            {
                "electrum.blockstream.info",
                "electrum.qtornado.com",
                "bitcoin.aranguren.org",
                "electrum.emzy.de",
                "kirsche.emzy.de",
            },
            Timeout = 5000
        });

        // Mock log:
        this.logMessages = new List<string>();
        var mockLogger = new Mock<ILogger<ElectrumXService>>();
        mockLogger.Setup(x => x.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                var logLevel = (LogLevel)invocation.Arguments[0];
                var state = invocation.Arguments[2];
                var formatter = invocation.Arguments[4];
                var message = formatter.GetType()
                    .GetMethod("Invoke")?
                    .Invoke(formatter, new[] { state, null })?.ToString();
                if (message != null)
                {
                    Debug.WriteLine($"[{logLevel}] {message}");
                    logMessages.Add($"[{logLevel}] {message}");
                }
            }));

        // Create service:
        this.service = new ElectrumXService(mockLogger.Object, settings);
        
        // Start the service and wait for initialization (subscriptions setup)
        // This is necessary because ExecuteAsync sets up subscriptions asynchronously
        var startTask = this.service.StartAsync(CancellationToken.None);
        startTask.Wait(TimeSpan.FromSeconds(10)); // Wait for service to start and subscribe
        
        // Give subscriptions a moment to establish
        Thread.Sleep(1000);
    }


    public void Dispose() => service.Dispose();


    protected List<string> logMessages;
    protected ElectrumXService service;
}
public class ElectrumXTests_Constructor : ElectrumXTests
{
    [Fact]
    public void WithSingleHost()
    {
        // Arrange:
        var settings = Options.Create(new ElectrumXSettings
        {
            Hosts = [ "electrum.example.com" ],
            Port = 50001,
            Timeout = 10000
        });

        using var service = new ElectrumXService(logger.Object, settings);

        Assert.Single(service.ConfiguredClients);
        var host = service.ConfiguredClients.First();
        Assert.Equal("electrum.example.com", host.Hostname);
        Assert.Equal(50001, host.Port);
    }
    [Fact]
    public void WithMultipleHosts()
    {
        // Arrange:
        var settings = Options.Create(new ElectrumXSettings
        {
            Port = 50001,
            Hosts = new[]
            {
                "host1.example.com",
                "host2.example.com:50002",
                "",
                "  ",
                "host3.example.com"
            },
            Timeout = 5000
        });

        using var service = new ElectrumXService(logger.Object, settings);

        Assert.Equal(3, service.ConfiguredClients.Count);
        var hostList = service.ConfiguredClients.ToArray();
        // First host
        Assert.Equal("host1.example.com", hostList[0].Hostname);
        Assert.Equal(50001, hostList[0].Port);
        Assert.Equal(5000, hostList[0].Timeout);
        // Second host
        Assert.Equal("host2.example.com", hostList[1].Hostname);
        Assert.Equal(50002, hostList[1].Port);
        Assert.Equal(5000, hostList[1].Timeout);
        // Third host
        Assert.Equal("host3.example.com", hostList[2].Hostname);
        Assert.Equal(50001, hostList[2].Port); // Uses default port
        Assert.Equal(5000, hostList[2].Timeout);
    }
    [Fact]
    public void WithDefaultSettings()
    {
        // Arrange: Unspecified host should trigger default settings

        using var service = new ElectrumXService(logger.Object);

        Assert.Single(service.ConfiguredClients);
        var host = service.ConfiguredClients.First();
        Assert.Equal("electrum.blockstream.info", host.Hostname);
        Assert.Equal(50001, host.Port);
    }
}
public class ElectrumXTests_HostRotation : CommunicativeElectrumXTests
{
    #region Constants
    private const string ReceivedBlock = "Received 📦 block";
    private const string StaleBlock = "Cached block";
    #endregion


    [Fact]
    [Trait("Category", "Communication")]
    public async Task SubscriptionBasedUpdates()
    {
        // With subscription-based architecture, blocks are pushed from the server
        // GetCurrentBlockAsync simply returns the cached LastBlock
        
        // Wait a moment for subscription to be established
        await Task.Delay(TimeSpan.FromSeconds(2));
        
        // Multiple requests should all return the same cached block instantly
        for (int i = 0; i < 5; i++)
        {
            logMessages.Clear();
            LogTest($"Requesting block {i + 1}");
            var block = await service.GetCurrentBlockAsync(CancellationToken.None);
            Assert.NotNull(block);
            
            // All requests use the cached block from subscription
            Assert.True(block.Height > 0, "Should have a valid block height");
        }

        // Log the subscription status
        var hasReceivedBlock = logMessages.Any(m => m.Contains(ReceivedBlock, StringComparison.OrdinalIgnoreCase));
        var hasStaleWarning = logMessages.Any(m => m.Contains(StaleBlock, StringComparison.OrdinalIgnoreCase));
        
        LogTest($"Test completed. Subscription is providing block updates: {service.ReceivedBlocks > 0}");
        LogTest($"Total blocks received via subscription: {service.ReceivedBlocks}");
    }
}