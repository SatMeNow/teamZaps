using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using teamZaps.Backend;
using teamZaps.Configuration;

namespace teamZaps.Tests.Backend;


public abstract class ElectrumXTests : BackendUnitTest<ElectrumXService>
{
}
public class ElectrumXTests_Constructor : ElectrumXTests
{
    [Fact]
    public void WithSingleHost()
    {
        // Arrange
        var settings = Options.Create(new ElectrumXSettings
        {
            Host = "electrum.example.com",
            Port = 50001,
            UseSsl = false,
            Timeout = 10000
        });

        // Act
        using var service = new ElectrumXService(logger.Object, settings);

        // Assert
        Assert.Single(service.Hosts);
        var host = service.Hosts.First();
        Assert.Equal("electrum.example.com", host.Hostname);
        Assert.Equal(50001, host.Port);
    }
    [Fact]
    public void WithMultipleHosts()
    {
        // Arrange
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
            UseSsl = false,
            Timeout = 5000
        });

        // Act
        using var service = new ElectrumXService(logger.Object, settings);

        // Assert - Verify all hosts have the expected properties
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
        // Arrange - Unspecified host should trigger default settings

        // Act
        using var service = new ElectrumXService(logger.Object);

        // Assert - Should use default Blockstream server
        Assert.Single(service.Hosts);
        var host = service.Hosts.First();
        Assert.Equal("electrum.blockstream.info", host.Hostname);
        Assert.Equal(50001, host.Port);
    }
}