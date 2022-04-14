﻿namespace NServiceBus
{
    using System;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using NServiceBus.Logging;

    /// <summary>
    /// Extension methods to configure the bridge for the .NET Core generic host.
    /// </summary>
    public static class HostBuilderExtensions
    {
        /// <summary>
        /// Configures the host to start the bridge
        /// </summary>
        public static IHostBuilder UseNServiceBusBridge(
            this IHostBuilder hostBuilder,
            Action<BridgeConfiguration> bridgeConfigurationAction)
        {
            return hostBuilder.UseNServiceBusBridge((_, rc) => bridgeConfigurationAction(rc));
        }

        /// <summary>
        /// Configures the host to start the bridge
        /// </summary>
        public static IHostBuilder UseNServiceBusBridge(
            this IHostBuilder hostBuilder,
            Action<HostBuilderContext, BridgeConfiguration> bridgeConfigurationAction)
        {
            var deferredLoggerFactory = new DeferredLoggerFactory();
            LogManager.UseFactory(deferredLoggerFactory);

            hostBuilder.ConfigureServices((hostBuilderContext, serviceCollection) =>
            {
                var bridgeConfiguration = new BridgeConfiguration();

                bridgeConfigurationAction(hostBuilderContext, bridgeConfiguration);

                serviceCollection.AddSingleton(sp =>
                {
                    return bridgeConfiguration.FinalizeConfiguration(sp.GetRequiredService<ILogger<BridgeConfiguration>>());
                });

                serviceCollection.AddSingleton(deferredLoggerFactory);
                serviceCollection.AddSingleton<IHostedService, BridgeHostedService>();
                serviceCollection.AddSingleton<IStartableBridge, StartableBridge>();
                serviceCollection.AddSingleton<EndpointProxyFactory>();
                serviceCollection.AddSingleton<SubscriptionManager>();
                serviceCollection.AddSingleton<TargetEndpointDispatcherRegistry>();
                serviceCollection.AddSingleton<ITargetEndpointDispatcherRegistry>(sp => sp.GetRequiredService<TargetEndpointDispatcherRegistry>());
                serviceCollection.AddTransient<MessageShovel>();
            });

            return hostBuilder;
        }
    }
}