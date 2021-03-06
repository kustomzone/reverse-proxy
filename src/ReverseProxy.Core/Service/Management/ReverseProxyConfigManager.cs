// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.ReverseProxy.Common;
using Microsoft.ReverseProxy.Core.Abstractions;
using Microsoft.ReverseProxy.Core.ConfigModel;
using Microsoft.ReverseProxy.Core.RuntimeModel;
using Microsoft.ReverseProxy.Utilities;

namespace Microsoft.ReverseProxy.Core.Service.Management
{
    /// <summary>
    /// Default implementation of <see cref="IReverseProxyConfigManager"/>
    /// which provides a method to apply Proxy configuration changes
    /// by leveraging <see cref="IDynamicConfigBuilder"/>.
    /// </summary>
    internal class ReverseProxyConfigManager : IReverseProxyConfigManager
    {
        private readonly ILogger<ReverseProxyConfigManager> _logger;
        private readonly IDynamicConfigBuilder _configBuilder;
        private readonly IRuntimeRouteBuilder _routeEndpointBuilder;
        private readonly IBackendManager _backendManager;
        private readonly IRouteManager _routeManager;
        private readonly IProxyDynamicEndpointDataSource _dynamicEndpointDataSource;

        public ReverseProxyConfigManager(
            ILogger<ReverseProxyConfigManager> logger,
            IDynamicConfigBuilder configBuilder,
            IRuntimeRouteBuilder routeEndpointBuilder,
            IBackendManager backendManager,
            IRouteManager routeManager,
            IProxyDynamicEndpointDataSource dynamicEndpointDataSource)
        {
            Contracts.CheckValue(logger, nameof(logger));
            Contracts.CheckValue(configBuilder, nameof(configBuilder));
            Contracts.CheckValue(routeEndpointBuilder, nameof(routeEndpointBuilder));
            Contracts.CheckValue(backendManager, nameof(backendManager));
            Contracts.CheckValue(routeManager, nameof(routeManager));
            Contracts.CheckValue(dynamicEndpointDataSource, nameof(dynamicEndpointDataSource));

            _logger = logger;
            _configBuilder = configBuilder;
            _routeEndpointBuilder = routeEndpointBuilder;
            _backendManager = backendManager;
            _routeManager = routeManager;
            _dynamicEndpointDataSource = dynamicEndpointDataSource;
        }

        /// <inheritdoc/>
        public async Task<bool> ApplyConfigurationsAsync(IConfigErrorReporter configErrorReporter, CancellationToken cancellation)
        {
            if (configErrorReporter == null)
            {
                throw new ArgumentNullException(nameof(configErrorReporter));
            }

            var configResult = await _configBuilder.BuildConfigAsync(configErrorReporter, cancellation);
            if (!configResult.IsSuccess)
            {
                return false;
            }

            var config = configResult.Value;
            UpdateRuntimeBackends(config);
            UpdateRuntimeRoutes(config);

            return true;
        }

        private void UpdateRuntimeBackends(DynamicConfigRoot config)
        {
            var desiredBackends = new HashSet<string>(StringComparer.Ordinal);
            foreach (var configBackendPair in config.Backends)
            {
                var configBackend = configBackendPair.Value;
                desiredBackends.Add(configBackendPair.Key);

                _backendManager.GetOrCreateItem(
                    itemId: configBackendPair.Key,
                    setupAction: backend =>
                    {
                        UpdateRuntimeEndpoints(configBackend.Endpoints, backend.EndpointManager);

                        var newConfig = new BackendConfig(
                                new BackendConfig.BackendHealthCheckOptions(
                                    enabled: configBackend.HealthCheckOptions?.Enabled ?? false,
                                    interval: configBackend.HealthCheckOptions?.Interval ?? TimeSpan.FromSeconds(0),
                                    timeout: configBackend.HealthCheckOptions?.Timeout ?? TimeSpan.FromSeconds(0),
                                    port: configBackend.HealthCheckOptions?.Port ?? 0,
                                    path: configBackend.HealthCheckOptions?.Path ?? string.Empty),
                                new BackendConfig.BackendLoadBalancingOptions(
                                    mode: configBackend.LoadBalancing?.Mode ?? default));

                        var currentBackendConfig = backend.Config.Value;
                        if (currentBackendConfig == null ||
                            currentBackendConfig.HealthCheckOptions.Enabled != newConfig.HealthCheckOptions.Enabled ||
                            currentBackendConfig.HealthCheckOptions.Interval != newConfig.HealthCheckOptions.Interval ||
                            currentBackendConfig.HealthCheckOptions.Timeout != newConfig.HealthCheckOptions.Timeout ||
                            currentBackendConfig.HealthCheckOptions.Port != newConfig.HealthCheckOptions.Port ||
                            currentBackendConfig.HealthCheckOptions.Path != newConfig.HealthCheckOptions.Path)
                        {
                            if (currentBackendConfig == null)
                            {
                                Log.BackendAdded(_logger, configBackendPair.Key);
                            }
                            else
                            {
                                Log.BackendChanged(_logger, configBackendPair.Key);
                            }

                            // Config changed, so update runtime backend
                            backend.Config.Value = newConfig;
                        }
                    });
            }

            foreach (var existingBackend in _backendManager.GetItems())
            {
                if (!desiredBackends.Contains(existingBackend.BackendId))
                {
                    // NOTE 1: This is safe to do within the `foreach` loop
                    // because `IBackendManager.GetItems` returns a copy of the list of backends.
                    //
                    // NOTE 2: Removing the backend from `IBackendManager` is safe and existing
                    // ASP .NET Core endpoints will continue to work with their existing behavior (until those endpoints are updated)
                    // and the Garbage Collector won't destroy this backend object while it's referenced elsewhere.
                    Log.BackendRemoved(_logger, existingBackend.BackendId);
                    _backendManager.TryRemoveItem(existingBackend.BackendId);
                }
            }
        }

        private void UpdateRuntimeEndpoints(IDictionary<string, BackendEndpoint> configEndpoints, IEndpointManager endpointManager)
        {
            var desiredEndpoints = new HashSet<string>(StringComparer.Ordinal);
            foreach (var configEndpoint in configEndpoints)
            {
                desiredEndpoints.Add(configEndpoint.Key);
                endpointManager.GetOrCreateItem(
                    itemId: configEndpoint.Key,
                    setupAction: endpoint =>
                    {
                        if (endpoint.Config.Value?.Address != configEndpoint.Value.Address)
                        {
                            if (endpoint.Config.Value == null)
                            {
                                Log.EndpointAdded(_logger, configEndpoint.Key);
                            }
                            else
                            {
                                Log.EndpointChanged(_logger, configEndpoint.Key);
                            }
                            endpoint.Config.Value = new EndpointConfig(configEndpoint.Value.Address);
                        }
                    });
            }

            foreach (var existingEndpoint in endpointManager.GetItems())
            {
                if (!desiredEndpoints.Contains(existingEndpoint.EndpointId))
                {
                    // NOTE 1: This is safe to do within the `foreach` loop
                    // because `IEndpointManager.GetItems` returns a copy of the list of endpoints.
                    //
                    // NOTE 2: Removing the endpoint from `IEndpointManager` is safe and existing
                    // backends will continue to work with their existing behavior (until those backends are updated)
                    // and the Garbage Collector won't destroy this backend object while it's referenced elsewhere.
                    Log.EndpointRemoved(_logger, existingEndpoint.EndpointId);
                    endpointManager.TryRemoveItem(existingEndpoint.EndpointId);
                }
            }
        }

        private void UpdateRuntimeRoutes(DynamicConfigRoot config)
        {
            var desiredRoutes = new HashSet<string>(StringComparer.Ordinal);
            var changed = false;

            foreach (var configRoute in config.Routes)
            {
                desiredRoutes.Add(configRoute.RouteId);

                // Note that this can be null, and that is fine. The resulting route may match
                // but would then fail to route, which is exactly what we were instructed to do in this case
                // since no valid backend was specified.
                var backendOrNull = _backendManager.TryGetItem(configRoute.BackendId);

                _routeManager.GetOrCreateItem(
                    itemId: configRoute.RouteId,
                    setupAction: route =>
                    {
                        var currentRouteConfig = route.Config.Value;
                        if (currentRouteConfig == null ||
                            currentRouteConfig.HasConfigChanged(configRoute, backendOrNull))
                        {
                            // Config changed, so update runtime route
                            changed = true;
                            if (currentRouteConfig == null)
                            {
                                Log.RouteAdded(_logger, configRoute.RouteId);
                            }
                            else
                            {
                                Log.RouteChanged(_logger, configRoute.RouteId);
                            }

                            var newConfig = _routeEndpointBuilder.Build(configRoute, backendOrNull, route);
                            route.Config.Value = newConfig;
                        }
                    });
            }

            foreach (var existingRoute in _routeManager.GetItems())
            {
                if (!desiredRoutes.Contains(existingRoute.RouteId))
                {
                    // NOTE 1: This is safe to do within the `foreach` loop
                    // because `IRouteManager.GetItems` returns a copy of the list of routes.
                    //
                    // NOTE 2: Removing the route from `IRouteManager` is safe and existing
                    // ASP .NET Core endpoints will continue to work with their existing behavior since
                    // their copy of `RouteConfig` is immutable and remains operational in whichever state is was in.
                    Log.RouteRemoved(_logger, existingRoute.RouteId);
                    _routeManager.TryRemoveItem(existingRoute.RouteId);
                    changed = true;
                }
            }

            if (changed)
            {
                var aspNetCoreEndpoints = new List<AspNetCore.Http.Endpoint>();
                foreach (var existingRoute in _routeManager.GetItems())
                {
                    var runtimeConfig = existingRoute.Config.Value;
                    if (runtimeConfig?.AspNetCoreEndpoints != null)
                    {
                        aspNetCoreEndpoints.AddRange(runtimeConfig.AspNetCoreEndpoints);
                    }
                }

                // This is where the new routes take effect!
                _dynamicEndpointDataSource.Update(aspNetCoreEndpoints);
            }
        }

        private static class Log
        {
            private static readonly Action<ILogger, string, Exception> _backendAdded = LoggerMessage.Define<string>(
                  LogLevel.Debug,
                  EventIds.BackendAdded,
                  "Backend `{backendId}` has been added.");

            private static readonly Action<ILogger, string, Exception> _backendChanged = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.BackendChanged,
                "Backend `{backendId}` has changed.");

            private static readonly Action<ILogger, string, Exception> _backendRemoved = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.BackendRemoved,
                "Backend `{backendId}` has been removed.");

            private static readonly Action<ILogger, string, Exception> _endpointAdded = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.EndpointAdded,
                "Endpoint `{endpointId}` has been added.");

            private static readonly Action<ILogger, string, Exception> _endpointChanged = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.EndpointChanged,
                "Endpoint `{endpointId}` has changed.");

            private static readonly Action<ILogger, string, Exception> _endpointRemoved = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.EndpointRemoved,
                "Endpoint `{endpointId}` has been removed.");

            private static readonly Action<ILogger, string, Exception> _routeAdded = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.RouteAdded,
                "Route `{routeId}` has been added.");

            private static readonly Action<ILogger, string, Exception> _routeChanged = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.RouteChanged,
                "Route `{routeId}` has changed.");

            private static readonly Action<ILogger, string, Exception> _routeRemoved = LoggerMessage.Define<string>(
                LogLevel.Debug,
                EventIds.RouteRemoved,
                "Route `{routeId}` has been removed.");

            public static void BackendAdded(ILogger logger, string backendId)
            {
                _backendAdded(logger, backendId, null);
            }

            public static void BackendChanged(ILogger logger, string backendId)
            {
                _backendChanged(logger, backendId, null);
            }

            public static void BackendRemoved(ILogger logger, string backendId)
            {
                _backendRemoved(logger, backendId, null);
            }

            public static void EndpointAdded(ILogger logger, string endpointId)
            {
                _endpointAdded(logger, endpointId, null);
            }

            public static void EndpointChanged(ILogger logger, string endpointId)
            {
                _endpointChanged(logger, endpointId, null);
            }

            public static void EndpointRemoved(ILogger logger, string endpointId)
            {
                _endpointRemoved(logger, endpointId, null);
            }

            public static void RouteAdded(ILogger logger, string routeId)
            {
                _routeAdded(logger, routeId, null);
            }

            public static void RouteChanged(ILogger logger, string routeId)
            {
                _routeChanged(logger, routeId, null);
            }

            public static void RouteRemoved(ILogger logger, string routeId)
            {
                _routeRemoved(logger, routeId, null);
            }
        }
    }
}
