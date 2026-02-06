using Testcontainers.ServiceBus;
using Xunit;

namespace ServiceBusToolset.Integration.Tests.Infrastructure;

public sealed class ServiceBusEmulatorFixture : IAsyncLifetime
{
    private readonly ServiceBusContainer _container = new ServiceBusBuilder()
                                                      .WithAcceptLicenseAgreement(true)
                                                      .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
