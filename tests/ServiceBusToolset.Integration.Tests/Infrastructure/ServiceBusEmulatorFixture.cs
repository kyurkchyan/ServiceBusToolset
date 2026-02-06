using Testcontainers.ServiceBus;
using Xunit;

namespace ServiceBusToolset.Integration.Tests.Infrastructure;

public sealed class ServiceBusEmulatorFixture : IAsyncLifetime
{
    private const int ManagementPort = 5300;

    private readonly ServiceBusContainer _container = new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.0")
                                                      .WithAcceptLicenseAgreement(true)
                                                      .Build();

    /// <summary>
    /// AMQP connection string for <see cref="Azure.Messaging.ServiceBus.ServiceBusClient"/>.
    /// </summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// HTTP connection string for <see cref="Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient"/>,
    /// which uses HTTP on port 5300 instead of AMQP on port 5672.
    /// </summary>
    public string AdministrationConnectionString =>
        $"Endpoint=sb://{_container.Hostname}:{_container.GetMappedPublicPort(ManagementPort)};"
        + "SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
