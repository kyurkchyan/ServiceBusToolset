using Azure.Messaging.ServiceBus;
using Testcontainers.ServiceBus;
using Xunit;

namespace ServiceBusToolset.Integration.Tests.Infrastructure;

public sealed class ServiceBusEmulatorFixture : IAsyncLifetime
{
    private readonly ServiceBusContainer _container = new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.0")
                                                      .WithAcceptLicenseAgreement(true)
                                                      .Build();

    /// <summary>
    /// AMQP connection string for <see cref="ServiceBusClient"/>.
    /// </summary>
    public string ConnectionString => _container.GetConnectionString();

    private const int ManagementPort = 5300;

    /// <summary>
    /// HTTP connection string for <see cref="Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient"/>,
    /// which uses HTTP on port 5300 instead of AMQP on port 5672.
    /// </summary>
    public string AdministrationConnectionString =>
        $"Endpoint=sb://{_container.Hostname}:{_container.GetMappedPublicPort(ManagementPort)};"
        + "SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

    /// <summary>
    /// Shared <see cref="ServiceBusClient"/> for test helper methods (dead-letter, populate, etc.).
    /// Using a single client across all tests avoids exhausting the emulator's connection quota.
    /// </summary>
    public ServiceBusClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Client = new ServiceBusClient(_container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _container.DisposeAsync();
    }
}
