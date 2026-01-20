using Microsoft.Extensions.Logging;
using Moq;
using TeamZaps.Backends;


public abstract class UnitTest<T>
    where T : class
{
    public UnitTest()
    {
        this.logger = new Mock<ILogger<T>>();
    }

    
    protected readonly Mock<ILogger<T>> logger;
}

public abstract class BackendUnitTest<T> : UnitTest<T>
    where T : class, IBackend
{
}