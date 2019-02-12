// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Microsoft.AspNetCore.Components.Browser.Rendering
{
    internal class RemoteRenderer : Renderer
    {
        // The purpose of the timeout is just to ensure server resources are released at some
        // point if the client disconnects without sending back an ACK after a render
        private const int TimeoutMilliseconds = 60 * 1000;

        private readonly int _id;
        private readonly IClientProxy _client;
        private readonly IJSRuntime _jsRuntime;
        private readonly RendererRegistry _rendererRegistry;
        private readonly ConcurrentDictionary<long, AutoCancelTaskCompletionSource<object>> _pendingRenders
            = new ConcurrentDictionary<long, AutoCancelTaskCompletionSource<object>>();
        private readonly ILogger _logger;
        private long _nextRenderId = 1;

        /// <summary>
        /// Notifies when a rendering exception occured.
        /// </summary>
        public event EventHandler<Exception> UnhandledException;

        /// <summary>
        /// Creates a new <see cref="RemoteRenderer"/>.
        /// </summary>
        /// <param name="serviceProvider">The <see cref="IServiceProvider"/>.</param>
        /// <param name="rendererRegistry">The <see cref="RendererRegistry"/>.</param>
        /// <param name="jsRuntime">The <see cref="IJSRuntime"/>.</param>
        /// <param name="client">The <see cref="IClientProxy"/>.</param>
        /// <param name="syncContext">A <see cref="SynchronizationContext"/> that can be used to serialize renderer operations.</param>
        public RemoteRenderer(
            IServiceProvider serviceProvider,
            RendererRegistry rendererRegistry,
            IJSRuntime jsRuntime,
            IClientProxy client,
            IDispatcher dispatcher,
            ILogger logger)
            : base(serviceProvider, dispatcher)
        {
            _rendererRegistry = rendererRegistry;
            _jsRuntime = jsRuntime;
            _client = client;

            _id = _rendererRegistry.Add(this);
            _logger = logger;
        }

        /// <summary>
        /// Attaches a new root component to the renderer,
        /// causing it to be displayed in the specified DOM element.
        /// </summary>
        /// <typeparam name="TComponent">The type of the component.</typeparam>
        /// <param name="domElementSelector">A CSS selector that uniquely identifies a DOM element.</param>
        public void AddComponent<TComponent>(string domElementSelector)
            where TComponent : IComponent
        {
            AddComponent(typeof(TComponent), domElementSelector);
        }

        /// <summary>
        /// Associates the <see cref="IComponent"/> with the <see cref="RemoteRenderer"/>,
        /// causing it to be displayed in the specified DOM element.
        /// </summary>
        /// <param name="componentType">The type of the component.</param>
        /// <param name="domElementSelector">A CSS selector that uniquely identifies a DOM element.</param>
        public void AddComponent(Type componentType, string domElementSelector)
        {
            var component = InstantiateComponent(componentType);
            var componentId = AssignRootComponentId(component);

            var attachComponentTask = _jsRuntime.InvokeAsync<object>(
                "Blazor._internal.attachRootComponentToElement",
                _id,
                domElementSelector,
                componentId);
            CaptureAsyncExceptions(attachComponentTask);

            RenderRootComponent(componentId);
        }

        /// <inheritdoc />
        protected override bool HandleException(Exception exception)
        {
            if (exception is AggregateException aggregateException)
            {
                foreach (var innerException in aggregateException.Flatten().InnerExceptions)
                {
                    _logger.UnhandledExceptionRenderingComponent(innerException);
                }
            }
            else
            {
                _logger.UnhandledExceptionRenderingComponent(exception);
            }

            return true;
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(true);
            _rendererRegistry.TryRemove(_id);
        }

        /// <inheritdoc />
        protected override Task UpdateDisplayAsync(in RenderBatch batch)
        {
            // Note that we have to capture the data as a byte[] synchronously here, because
            // SignalR's SendAsync can wait an arbitrary duration before serializing the params.
            // The RenderBatch buffer will get reused by subsequent renders, so we need to
            // snapshot its contents now.
            // TODO: Consider using some kind of array pool instead of allocating a new
            //       buffer on every render.
            var batchBytes = MessagePackSerializer.Serialize(batch, RenderBatchFormatterResolver.Instance);

            // Prepare to track the render process with a timeout
            var renderId = Interlocked.Increment(ref _nextRenderId);
            var pendingRenderInfo = new AutoCancelTaskCompletionSource<object>(TimeoutMilliseconds);
            _pendingRenders[renderId] = pendingRenderInfo;

            // Send the render batch to the client
            // If the "send" operation fails (synchronously or asynchronously), abort
            // the whole render with that exception
            try
            {
                _client.SendAsync("JS.RenderBatch", _id, renderId, batchBytes).ContinueWith(sendTask =>
                {
                    if (sendTask.IsFaulted)
                    {
                        pendingRenderInfo.TrySetException(sendTask.Exception);
                    }
                });
            }
            catch (Exception syncException)
            {
                pendingRenderInfo.TrySetException(syncException);
            }

            // When the render is completed (success, fail, or timeout), stop tracking it
            return pendingRenderInfo.Task.ContinueWith(task =>
            {
                _pendingRenders.TryRemove(renderId, out var ignored);
                if (task.IsFaulted)
                {
                    UnhandledException?.Invoke(this, task.Exception);
                }
            });
        }

        public void OnRenderCompleted(long renderId, string errorMessageOrNull)
        {
            if (_pendingRenders.TryGetValue(renderId, out var pendingRenderInfo))
            {
                if (errorMessageOrNull == null)
                {
                    pendingRenderInfo.TrySetResult(null);
                }
                else
                {
                    pendingRenderInfo.TrySetException(
                        new RemoteRendererException(errorMessageOrNull));
                }
            }
        }

        private void CaptureAsyncExceptions(Task task)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    UnhandledException?.Invoke(this, t.Exception);
                }
            });
        }
    }
}
