// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class ServiceProviderCache
    {
        private readonly ConcurrentDictionary<long, (IServiceProvider ServiceProvider, IDictionary<string, string> DebugInfo)> _configurations
            = new ConcurrentDictionary<long, (IServiceProvider, IDictionary<string, string>)>();

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public static ServiceProviderCache Instance { get; } = new ServiceProviderCache();

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual IServiceProvider GetOrAdd([NotNull] IDbContextOptions options, bool providerRequired)
        {
            var coreOptionsExtension = options.FindExtension<CoreOptionsExtension>();
            var internalServiceProvider = coreOptionsExtension?.InternalServiceProvider;
            if (internalServiceProvider != null)
            {
                ValidateOptions(options);

                var optionsInitializer = internalServiceProvider.GetService<ISingletonOptionsInitializer>();
                if (optionsInitializer == null)
                {
                    throw new InvalidOperationException(CoreStrings.NoEfServices);
                }

                if (providerRequired)
                {
                    optionsInitializer.EnsureInitialized(internalServiceProvider, options);
                }

                return internalServiceProvider;
            }

            (IServiceProvider ServiceProvider, IDictionary<string, string> DebugInfo) BuildServiceProvider()
            {
                ValidateOptions(options);

                var debugInfo = new Dictionary<string, string>();
                foreach (var optionsExtension in options.Extensions)
                {
                    optionsExtension.PopulateDebugInfo(debugInfo);
                }

                debugInfo = debugInfo.OrderBy(v => debugInfo.Keys).ToDictionary(d => d.Key, v => v.Value);

                var services = new ServiceCollection();
                var hasProvider = ApplyServices(options, services);

                var replacedServices = coreOptionsExtension?.ReplacedServices;
                if (replacedServices != null)
                {
                    // For replaced services we use the service collection to obtain the lifetime of
                    // the service to replace. The replaced services are added to a new collection, after
                    // which provider and core services are applied. This ensures that any patching happens
                    // to the replaced service.
                    var updatedServices = new ServiceCollection();
                    foreach (var descriptor in services)
                    {
                        if (replacedServices.TryGetValue(descriptor.ServiceType, out var replacementType))
                        {
                            ((IList<ServiceDescriptor>) updatedServices).Add(
                                new ServiceDescriptor(descriptor.ServiceType, replacementType, descriptor.Lifetime));
                        }
                    }

                    ApplyServices(options, updatedServices);
                    services = updatedServices;
                }

                var serviceProvider = services.BuildServiceProvider();

                if (hasProvider)
                {
                    serviceProvider
                        .GetRequiredService<ISingletonOptionsInitializer>()
                        .EnsureInitialized(serviceProvider, options);
                }

                using (var scope = serviceProvider.CreateScope())
                {
                    var logger =
                        scope.ServiceProvider.GetRequiredService<IDiagnosticsLogger<DbLoggerCategory.Infrastructure>>();

                    if (_configurations.Count == 0)
                    {
                        logger.ServiceProviderCreated(serviceProvider);
                    }
                    else
                    {
                        logger.ServiceProviderDebugInfo(
                            debugInfo,
                            _configurations.Values.Select(v => v.DebugInfo).ToList());

                        if (_configurations.Count >= 20)
                        {
                            logger.ManyServiceProvidersCreatedWarning(
                                _configurations.Values.Select(e => e.ServiceProvider).ToList());
                        }
                    }
                }

                return (serviceProvider, debugInfo);
            }

            if (coreOptionsExtension?.ServiceProviderCachingEnabled == false)
            {
                return BuildServiceProvider().ServiceProvider;
            }

            var key = options.Extensions
                .OrderBy(e => e.GetType().Name)
                .Aggregate(0L, (t, e) => (t * 397) ^ ((long)e.GetType().GetHashCode() * 397) ^ e.GetServiceProviderHashCode());

            return _configurations.GetOrAdd(key, k => BuildServiceProvider()).ServiceProvider;
        }

        private static void ValidateOptions(IDbContextOptions options)
        {
            foreach (var extension in options.Extensions)
            {
                extension.Validate(options);
            }
        }

        private static bool ApplyServices(IDbContextOptions options, ServiceCollection services)
        {
            var coreServicesAdded = false;

            foreach (var extension in options.Extensions)
            {
                if (extension.ApplyServices(services))
                {
                    coreServicesAdded = true;
                }
            }

            if (coreServicesAdded)
            {
                return true;
            }

            new EntityFrameworkServicesBuilder(services).TryAddCoreServices();

            return false;
        }
    }
}
