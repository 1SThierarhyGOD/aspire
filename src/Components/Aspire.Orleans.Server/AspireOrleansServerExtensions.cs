// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Clustering.AzureStorage;
using Orleans.Configuration;
using Orleans.Reminders.AzureStorage;
using Orleans.TestingHost.UnixSocketTransport;
using static Aspire.Orleans.Server.OrleansServerSettingConstants;

namespace Aspire.Orleans.Server;

public static class AspireOrleansServerExtensions
{
    public static IHostApplicationBuilder UseOrleansAspire(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOrleans(siloBuilder =>
        {
            var serverSettings = new OrleansServerSettings();
            builder.Configuration.GetSection("Grains").Bind(serverSettings);

            if (serverSettings.Clustering is { } clusteringSection)
            {
                ApplyClusteringSettings(builder, siloBuilder, clusteringSection);
            }

            if (serverSettings.GrainStorage is { Count: > 0 } grainStorageSection)
            {
                foreach (var (name, configuration) in grainStorageSection)
                {
                    ApplyGrainStorageSettings(builder, siloBuilder, name, configuration);
                }
            }

            if (serverSettings.Reminders is { } remindersSection)
            {
                ApplyRemindersSettings(builder, siloBuilder, remindersSection);
            }

            // Enable distributed tracing for open telemetry.
            siloBuilder.AddActivityPropagation();

            siloBuilder.UseAzureStorageClustering((OptionsBuilder<AzureStorageClusteringOptions> optionsBuilder) =>
            {
                optionsBuilder.Configure<TableServiceClient>(
                    (options, tableClient) => options.ConfigureTableServiceClient(() => Task.FromResult(tableClient)));
            });

            siloBuilder.AddAzureBlobGrainStorageAsDefault((OptionsBuilder<AzureBlobStorageOptions> optionsBuilder) =>
            {
                optionsBuilder.Configure<BlobServiceClient>(
                    (options, blobClient) => options.ConfigureBlobServiceClient(() => Task.FromResult(blobClient)));
            });

            // BEGIN: will work only locally for now
            siloBuilder.Configure<EndpointOptions>(options =>
            {
                var rnd = new Random();
                options.SiloPort = rnd.Next(0, 65535);
                options.GatewayPort = rnd.Next(0, 65535);
            });
            siloBuilder.UseUnixSocketConnection();
            // END: will work only locally for now
        });

        return builder;
    }

    private static void ApplyGrainStorageSettings(IHostApplicationBuilder builder, ISiloBuilder siloBuilder, string name, IConfigurationSection configuration)
    {
        var connectionSettings = new ConnectionSettings();
        configuration.Bind(connectionSettings);

        var type = connectionSettings.ConnectionType;
        var connectionName = connectionSettings.ConnectionName;

        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException(message: $"A \"ConnectionType\" value must be specified for \"GrainStorage\" named '{name}'.", innerException: null);
        }

        if (string.IsNullOrWhiteSpace(connectionName))
        {
            throw new ArgumentException(message: $"A \"ConnectionName\" value must be specified for \"GrainStorage\" named '{name}'.", innerException: null);
        }

        if (string.Equals(InternalType, type, StringComparison.OrdinalIgnoreCase))
        {
            siloBuilder.AddMemoryGrainStorage(name);
        }
        else if (string.Equals(AzureTablesType, type, StringComparison.OrdinalIgnoreCase))
        {
            // Configure a table service client in the dependency injection container.
            builder.AddKeyedAzureTableService(connectionName);

            // Configure Orleans to use the configured table service client.
            siloBuilder.AddAzureTableGrainStorage(name, optionsBuilder => optionsBuilder.Configure(
                (AzureTableStorageOptions options, IServiceProvider serviceProvider) =>
                {
                    var tableServiceClient = Task.FromResult(serviceProvider.GetRequiredKeyedService<TableServiceClient>(connectionName));
                    options.ConfigureTableServiceClient(() => tableServiceClient);
                }));
        }
        else if (string.Equals(AzureBlobsType, type, StringComparison.OrdinalIgnoreCase))
        {
            // Configure a blob service client in the dependency injection container.
            builder.AddKeyedAzureBlobService(connectionName);

            // Configure Orleans to use the configured table service client.
            siloBuilder.AddAzureBlobGrainStorage(name, optionsBuilder => optionsBuilder.Configure(
                (AzureBlobStorageOptions options, IServiceProvider serviceProvider) =>
                {
                    var tableServiceClient = Task.FromResult(serviceProvider.GetRequiredKeyedService<BlobServiceClient>(connectionName));
                    options.ConfigureBlobServiceClient(() => tableServiceClient);
                }));
        }
        else
        {
            throw new NotSupportedException($"Unsupported connection type \"{type}\".");
        }
    }

    private static void ApplyClusteringSettings(IHostApplicationBuilder builder, ISiloBuilder siloBuilder, IConfigurationSection configuration)
    {
        var connectionSettings = new ConnectionSettings();
        configuration.Bind(connectionSettings);

        var type = connectionSettings.ConnectionType;
        var connectionName = connectionSettings.ConnectionName;

        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException(message: "A value must be specified for \"Clustering.ConnectionType\".", innerException: null);
        }

        if (string.IsNullOrWhiteSpace(connectionName))
        {
            throw new ArgumentException(message: "A value must be specified for \"Clustering.ConnectionName\".", innerException: null);
        }

        if (string.Equals(InternalType, type, StringComparison.OrdinalIgnoreCase))
        {
            siloBuilder.UseLocalhostClustering();
            var connectionString = builder.Configuration.GetConnectionString(connectionName);

            if (connectionString is null || !IPEndPoint.TryParse(connectionString, out var primarySiloEndPoint))
            {
                throw new InvalidOperationException($"Invalid connection string specified for '{connectionName}'.");
            }
        }
        else if (string.Equals(AzureTablesType, type, StringComparison.OrdinalIgnoreCase))
        {
            // Configure a table service client in the dependency injection container.
            builder.AddKeyedAzureTableService(connectionName);

            // Configure Orleans to use the configured table service client.
            siloBuilder.UseAzureStorageClustering(optionsBuilder => optionsBuilder.Configure(
                (AzureStorageClusteringOptions options, IServiceProvider serviceProvider) =>
                {
                    var tableServiceClient = Task.FromResult(serviceProvider.GetRequiredKeyedService<TableServiceClient>(connectionName));
                    options.ConfigureTableServiceClient(() => tableServiceClient);
                }));
        }
        else
        {
            throw new NotSupportedException($"Unsupported connection type \"{type}\".");
        }
    }

    private static void ApplyRemindersSettings(IHostApplicationBuilder builder, ISiloBuilder siloBuilder, IConfigurationSection configuration)
    {
        var connectionSettings = new ConnectionSettings();
        configuration.Bind(connectionSettings);

        siloBuilder.AddReminders();
        var type = connectionSettings.ConnectionType;
        var connectionName = connectionSettings.ConnectionName;

        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException(message: "A value must be specified for \"Reminders.ConnectionType\".", innerException: null);
        }

        if (string.IsNullOrWhiteSpace(connectionName))
        {
            throw new ArgumentException(message: "A value must be specified for \"Reminders.ConnectionName\".", innerException: null);
        }

        if (string.Equals(InternalType, type, StringComparison.OrdinalIgnoreCase))
        {
            siloBuilder.UseInMemoryReminderService();
            var connectionString = builder.Configuration.GetConnectionString(connectionName);

            if (connectionString is null || !IPEndPoint.TryParse(connectionString, out var primarySiloEndPoint))
            {
                throw new InvalidOperationException($"Invalid connection string specified for '{connectionName}'.");
            }
        }
        else if (string.Equals(AzureTablesType, type, StringComparison.OrdinalIgnoreCase))
        {
            // Configure a table service client in the dependency injection container.
            builder.AddKeyedAzureTableService(connectionName);

            // Configure Orleans to use the configured table service client.
            siloBuilder.UseAzureTableReminderService(optionsBuilder => optionsBuilder.Configure(
                (AzureTableReminderStorageOptions options, IServiceProvider serviceProvider) =>
                {
                    var tableServiceClient = Task.FromResult(serviceProvider.GetRequiredKeyedService<TableServiceClient>(connectionName));
                    options.ConfigureTableServiceClient(() => tableServiceClient);
                }));
        }
        else
        {
            throw new NotSupportedException($"Unsupported connection type \"{type}\".");
        }
    }
}
