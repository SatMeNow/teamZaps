using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using TeamZaps.Backends;
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
            Host = "electrum.example.com",
            Port = 50001,
            Timeout = 10000
        });

        using var service = new ElectrumXService(logger.Object, settings);

        Assert.Single(service.Hosts);
        var host = service.Hosts.First();
        Assert.Equal("electrum.example.com", host.Hostname);
        Assert.Equal(50001, host.Port);
    }
    [Fact]
    public void WithMultipleHosts()
    {
        // Arrange:
        var settings = Options.Create(new ElectrumXSettings
        {
            Host = "host1.example.com",
            Port = 50001,
            Hosts = new[]
            {
                "host2.example.com:50002",
                "",
                "  ",
                "host3.example.com"
            },
            Timeout = 5000
        });

        using var service = new ElectrumXService(logger.Object, settings);

        Assert.Equal(3, service.Hosts.Count);
        var hostList = service.Hosts.ToArray();
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

        Assert.Single(service.Hosts);
        var host = service.Hosts.First();
        Assert.Equal("electrum.blockstream.info", host.Hostname);
        Assert.Equal(50001, host.Port);
    }
}
public class ElectrumXTests_HostRotation : CommunicativeElectrumXTests
{
    #region Constants
    private const string AllHostsRecentlyUsed = "all hosts were recently used";
    private const string ReceivedNewBlock = "Received new block";
    private const string KeepUsingLastBlock = "Keep using last block";
    #endregion


    [Fact]
    [Trait("Category", "Communication")]
    public async Task RecentlyUsedHostsFor30Seconds()
    {
        var testWatch = Stopwatch.StartNew();

        // First 5 requests should succeed (one per host):
        // Each host is used once and becomes recently used
        // Service rotates to next fresh host for each request:
        for (int i = 0; i < 5; i++)
        {
            logMessages.Clear();
            LogTest($"Requesting block {i + 1}");
            var block = await service.GetCurrentBlockAsync(CancellationToken.None);
            Assert.NotNull(block);
            Assert.DoesNotContain(logMessages, m => m.Contains(AllHostsRecentlyUsed, StringComparison.OrdinalIgnoreCase));
        }

        // Sixth request (within 30s):
        // All hosts are now recently used, should return last block from cache:
        logMessages.Clear();
        LogTest("Requesting block 6");
        var block6 = await service.GetCurrentBlockAsync(CancellationToken.None);
        Assert.NotNull(block6);
        Assert.Single(logMessages, m => m.Contains(AllHostsRecentlyUsed, StringComparison.OrdinalIgnoreCase));

        // [Debug] Expect that we are within reuse delay threshold:
        Assert.InRange(testWatch.Elapsed, TimeSpan.Zero, ElectrumXClient.ReuseDelay);
        // Wait for reuse delay period to expire (30s + 3s buffer):
        var timeToWait = (ElectrumXClient.ReuseDelay + TimeSpan.FromSeconds(3) - testWatch.Elapsed);
        await Task.Delay(timeToWait);

        // Seventh request (after 30s):
        // First host should be fresh again and make a real request:
        logMessages.Clear();
        LogTest("Requesting block 7");
        var block7 = await service.GetCurrentBlockAsync(CancellationToken.None);
        Assert.NotNull(block7);
        Assert.DoesNotContain(logMessages, m => m.Contains(AllHostsRecentlyUsed, StringComparison.OrdinalIgnoreCase));
        
        // Should contain either "Received new block" or "Keep using last block"
        // depending on whether blockchain had a new block (both are valid outcomes):
        var hasReceivedNewBlock = logMessages.Any(m => m.Contains(ReceivedNewBlock, StringComparison.OrdinalIgnoreCase));
        var hasKeepUsingBlock = logMessages.Any(m => m.Contains(KeepUsingLastBlock, StringComparison.OrdinalIgnoreCase));
        Assert.True(hasReceivedNewBlock || hasKeepUsingBlock,
            $"Expected either '{ReceivedNewBlock}' or '{KeepUsingLastBlock}' in logs");
    }
}