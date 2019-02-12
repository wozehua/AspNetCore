// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using Microsoft.AspNetCore.Components.Browser;
using Microsoft.AspNetCore.Components.Browser.Rendering;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Components.Server.Circuits
{
    internal class DefaultCircuitFactory : CircuitFactory
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly DefaultCircuitFactoryOptions _options;
        private readonly ILoggerFactory _loggerFactory;

        public DefaultCircuitFactory(
            IServiceScopeFactory scopeFactory,
            IOptions<DefaultCircuitFactoryOptions> options,
            ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _options = options.Value;
            _loggerFactory = loggerFactory;
        }

        public override CircuitHost CreateCircuitHost(HttpContext httpContext, IClientProxy client)
        {
            if (!_options.StartupActions.TryGetValue(httpContext.Request.Path, out var config))
            {
                var message = $"Could not find an ASP.NET Core Components startup action for request path '{httpContext.Request.Path}'.";
                throw new InvalidOperationException(message);
            }

            var scope = _scopeFactory.CreateScope();
            var jsRuntime = new RemoteJSRuntime(client);
            var rendererRegistry = new RendererRegistry();
            var synchronizationContext = new CircuitSynchronizationContext();
            var renderer = new RemoteRenderer(
                scope.ServiceProvider,
                rendererRegistry,
                jsRuntime,
                client,
                dispatcher,
                _loggerFactory.CreateLogger<RemoteRenderer>());

            var circuitHandlers = scope.ServiceProvider.GetServices<CircuitHandler>()
                .OrderBy(h => h.Order)
                .ToArray();

            var circuitHost = new CircuitHost(
                scope,
                client,
                rendererRegistry,
                renderer,
                config,
                jsRuntime,
                circuitHandlers);

            // Initialize per-circuit data that services need
            (circuitHost.Services.GetRequiredService<IJSRuntimeAccessor>() as DefaultJSRuntimeAccessor).JSRuntime = jsRuntime;
            (circuitHost.Services.GetRequiredService<ICircuitAccessor>() as DefaultCircuitAccessor).Circuit = circuitHost.Circuit;

            return circuitHost;
        }
    }
}
