// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.RenderTree;

namespace Microsoft.AspNetCore.Components.Rendering
{
    /// <summary>
    /// Provides mechanisms for rendering hierarchies of <see cref="IComponent"/> instances,
    /// dispatching events to them, and notifying when the user interface is being updated.
    /// </summary>
    public abstract class Renderer : IDisposable
    {
        private readonly ComponentFactory _componentFactory;
        private readonly Dictionary<int, ComponentState> _componentStateById = new Dictionary<int, ComponentState>();
        private readonly RenderBatchBuilder _batchBuilder = new RenderBatchBuilder();
        private readonly Dictionary<int, EventHandlerInvoker> _eventBindings = new Dictionary<int, EventHandlerInvoker>();
        private IDispatcher _dispatcher;

        private int _nextComponentId = 0; // TODO: change to 'long' when Mono .NET->JS interop supports it
        private bool _isBatchInProgress;
        private int _lastEventHandlerId = 0;
        private List<Task> _pendingTasks;

        /// <summary>
        /// Allows the caller to handle exceptions from the SynchronizationContext when one is available.
        /// </summary>
        public event UnhandledExceptionEventHandler UnhandledSynchronizationException
        {
            add
            {
                if (!(_dispatcher is RendererSynchronizationContext rendererSynchronizationContext))
                {
                    return;
                }
                rendererSynchronizationContext.UnhandledException += value;
            }
            remove
            {
                if (!(_dispatcher is RendererSynchronizationContext rendererSynchronizationContext))
                {
                    return;
                }
                rendererSynchronizationContext.UnhandledException -= value;
            }
        }

        /// <summary>
        /// Constructs an instance of <see cref="Renderer"/>.
        /// </summary>
        /// <param name="serviceProvider">The <see cref="IServiceProvider"/> to be used when initializing components.</param>
        public Renderer(IServiceProvider serviceProvider)
        {
            _componentFactory = new ComponentFactory(serviceProvider);
        }

        /// <summary>
        /// Constructs an instance of <see cref="Renderer"/>.
        /// </summary>
        /// <param name="serviceProvider">The <see cref="IServiceProvider"/> to be used when initializing components.</param>
        /// <param name="dispatcher">The <see cref="IDispatcher"/> to be for invoking user actions into the <see cref="Renderer"/> context.</param>
        public Renderer(IServiceProvider serviceProvider, IDispatcher dispatcher) : this(serviceProvider)
        {
            _dispatcher = dispatcher;
        }

        /// <summary>
        /// Creates an <see cref="IDispatcher"/> that can be used with one or more <see cref="Renderer"/>.
        /// </summary>
        /// <returns>The <see cref="IDispatcher"/>.</returns>
        public static IDispatcher CreateDefaultDispatcher() => new RendererSynchronizationContext();

        /// <summary>
        /// Constructs a new component of the specified type.
        /// </summary>
        /// <param name="componentType">The type of the component to instantiate.</param>
        /// <returns>The component instance.</returns>
        protected IComponent InstantiateComponent(Type componentType)
            => _componentFactory.InstantiateComponent(componentType);

        /// <summary>
        /// Associates the <see cref="IComponent"/> with the <see cref="Renderer"/>, assigning
        /// an identifier that is unique within the scope of the <see cref="Renderer"/>.
        /// </summary>
        /// <param name="component">The component.</param>
        /// <returns>The component's assigned identifier.</returns>
        protected int AssignRootComponentId(IComponent component)
            => AttachAndInitComponent(component, -1).ComponentId;

        /// <summary>
        /// Gets the current render tree for a given component.
        /// </summary>
        /// <param name="componentId">The id for the component.</param>
        /// <returns>The <see cref="RenderTreeBuilder"/> representing the current render tree.</returns>
        private protected ArrayRange<RenderTreeFrame> GetCurrentRenderTreeFrames(int componentId) => GetRequiredComponentState(componentId).CurrrentRenderTree.GetFrames();

        /// <summary>
        /// Performs the first render for a root component. After this, the root component
        /// makes its own decisions about when to re-render, so there is no need to call
        /// this more than once.
        /// </summary>
        /// <param name="componentId">The ID returned by <see cref="AssignRootComponentId(IComponent)"/>.</param>
        protected void RenderRootComponent(int componentId)
        {
            RenderRootComponent(componentId, ParameterCollection.Empty);
        }

        /// <summary>
        /// Performs the first render for a root component. After this, the root component
        /// makes its own decisions about when to re-render, so there is no need to call
        /// this more than once.
        /// </summary>
        /// <param name="componentId">The ID returned by <see cref="AssignRootComponentId(IComponent)"/>.</param>
        /// <param name="initialParameters">The <see cref="ParameterCollection"/>with the initial parameters to use for rendering.</param>
        protected async void RenderRootComponent(int componentId, ParameterCollection initialParameters)
        {
            await RenderRootComponentAsync(componentId, initialParameters);
        }

        /// <summary>
        /// Allows derived types to handle exceptions during rendering. When unhandled, the exception is rethrown.
        /// </summary>
        /// <param name="componentId">The component Id.</param>
        /// <param name="component">The <see cref="IComponent"/> instance.</param>
        /// <param name="exception">The <see cref="Exception"/>.</param>
        /// <returns>
        /// <see langword="true"/> if <paramref name="exception"/> was handled, otherwise <see langword="false"/>.
        /// </returns>
        protected virtual bool HandleException(int componentId, IComponent component, Exception exception) => false;

        /// <summary>
        /// Performs the first render for a root component, waiting for this component and all
        /// children components to finish rendering in case there is any asynchronous work being
        /// done by any of the components. After this, the root component
        /// makes its own decisions about when to re-render, so there is no need to call
        /// this more than once.
        /// </summary>
        /// <param name="componentId">The ID returned by <see cref="AssignRootComponentId(IComponent)"/>.</param>
        protected Task RenderRootComponentAsync(int componentId)
        {
            return RenderRootComponentAsync(componentId, ParameterCollection.Empty);
        }

        /// <summary>
        /// Performs the first render for a root component, waiting for this component and all
        /// children components to finish rendering in case there is any asynchronous work being
        /// done by any of the components. After this, the root component
        /// makes its own decisions about when to re-render, so there is no need to call
        /// this more than once.
        /// </summary>
        /// <param name="componentId">The ID returned by <see cref="AssignRootComponentId(IComponent)"/>.</param>
        /// <param name="initialParameters">The <see cref="ParameterCollection"/>with the initial parameters to use for rendering.</param>
        protected async Task RenderRootComponentAsync(int componentId, ParameterCollection initialParameters)
        {
            if (Interlocked.CompareExchange(ref _pendingTasks, new List<Task>(), null) != null)
            {
                throw new InvalidOperationException("There is an ongoing rendering in progress.");
            }

            // During the rendering process we keep a list of components performing work in _pendingTasks.
            // _renderer.AddToPendingTasks will be called by ComponentState.SetDirectParameters to add the
            // the Task produced by Component.SetParametersAsync to _pendingTasks in order to track the
            // remaining work.
            // During the synchronous rendering process we don't wait for the pending asynchronous
            // work to finish as it will simply trigger new renders that will be handled afterwards.
            // During the asynchronous rendering process we want to wait up untill al components have
            // finished rendering so that we can produce the complete output.
            var componentState = GetRequiredComponentState(componentId);
            componentState.SetDirectParameters(initialParameters);

            try
            {
                await ProcessAsynchronousWork(componentState);
                Debug.Assert(_pendingTasks.Count == 0);
            }
            finally
            {
                _pendingTasks = null;
            }
        }

        private async Task ProcessAsynchronousWork(ComponentState componentState)
        {
            // Child components SetParametersAsync are stored in the queue of pending tasks,
            // which might trigger further renders.
            while (_pendingTasks.Count > 0)
            {
                Task pendingWork;
                // Create a Task that represents the remaining ongoing work for the rendering process
                pendingWork = Task.WhenAll(_pendingTasks);

                // Clear all pending work.
                _pendingTasks.Clear();

                // new work might be added before we check again as a result of waiting for all
                // the child components to finish executing SetParametersAsync

                try
                {
                await pendingWork;
        }
                catch when (pendingWork.IsCanceled || HandleException(componentState.ComponentId, componentState.Component, pendingWork.Exception))
                {
                    // await will unwrap an AggregateException and return exactly one inner exception.
                    // We'll do our best to handle all inner exception instances.
                    // Note that the componentId is the root component Id which may be different than the
                    // component that produced the error.
                }
            }
        }

        private ComponentState AttachAndInitComponent(IComponent component, int parentComponentId)
        {
            var componentId = _nextComponentId++;
            var parentComponentState = GetOptionalComponentState(parentComponentId);
            var componentState = new ComponentState(this, componentId, component, parentComponentState);
            _componentStateById.Add(componentId, componentState);
            component.Configure(new RenderHandle(this, componentId));
            return componentState;
        }

        /// <summary>
        /// Updates the visible UI.
        /// </summary>
        /// <param name="renderBatch">The changes to the UI since the previous call.</param>
        /// <returns>A <see cref="Task"/> to represent the UI update process.</returns>
        protected abstract Task UpdateDisplayAsync(in RenderBatch renderBatch);

        /// <summary>
        /// Notifies the specified component that an event has occurred.
        /// </summary>
        /// <param name="componentId">The unique identifier for the component within the scope of this <see cref="Renderer"/>.</param>
        /// <param name="eventHandlerId">The <see cref="RenderTreeFrame.AttributeEventHandlerId"/> value from the original event attribute.</param>
        /// <param name="eventArgs">Arguments to be passed to the event handler.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous execution operation.</returns>
        public async Task DispatchEventAsync(int componentId, int eventHandlerId, UIEventArgs eventArgs)
        {
            EnsureSynchronizationContext();

            if (_eventBindings.TryGetValue(eventHandlerId, out var binding))
            {
                // The event handler might request multiple renders in sequence. Capture them
                // all in a single batch.
                var componentState = GetRequiredComponentState(componentId);
                Task task = null;
                try
                {
                    _isBatchInProgress = true;
                    task = componentState.DispatchEventAsync(binding, eventArgs);
                    await task;
                }
                catch (Exception ex) when (task.IsCanceled || HandleException(componentId, componentState.Component, ex))
                {
                    // Exception was a result of a canceled task or was handled
                }
                finally
                {
                    _isBatchInProgress = false;
                    ProcessRenderQueue();
                }
            }
            else
            {
                throw new ArgumentException($"There is no event handler with ID {eventHandlerId}");
            }
        }

        /// <summary>
        /// Executes the supplied work item on the renderer's
        /// synchronization context.
        /// </summary>
        /// <param name="workItem">The work item to execute.</param>
        public virtual Task Invoke(Action workItem)
        {
            // This is for example when we run on a system with a single thread, like WebAssembly.
            if (_dispatcher == null)
            {
                workItem();
                return Task.CompletedTask;
            }

            if (SynchronizationContext.Current == _dispatcher)
            {
                // This is an optimization for when the dispatcher is also a syncronization context, like in the default case.
                // No need to dispatch. Avoid deadlock by invoking directly.
                workItem();
                return Task.CompletedTask;
            }
            else
            {
                return _dispatcher.Invoke(workItem);
            }
        }

        /// <summary>
        /// Executes the supplied work item on the renderer's
        /// synchronization context.
        /// </summary>
        /// <param name="workItem">The work item to execute.</param>
        public virtual Task InvokeAsync(Func<Task> workItem)
        {
            // This is for example when we run on a system with a single thread, like WebAssembly.
            if (_dispatcher == null)
            {
                workItem();
                return Task.CompletedTask;
            }

            if (SynchronizationContext.Current == _dispatcher)
            {
                // This is an optimization for when the dispatcher is also a syncronization context, like in the default case.
                // No need to dispatch. Avoid deadlock by invoking directly.
                return workItem();
            }
            else
            {
                return _dispatcher.InvokeAsync(workItem);
            }
        }

        internal void InstantiateChildComponentOnFrame(ref RenderTreeFrame frame, int parentComponentId)
        {
            if (frame.FrameType != RenderTreeFrameType.Component)
            {
                throw new ArgumentException($"The frame's {nameof(RenderTreeFrame.FrameType)} property must equal {RenderTreeFrameType.Component}", nameof(frame));
            }

            if (frame.ComponentState != null)
            {
                throw new ArgumentException($"The frame already has a non-null component instance", nameof(frame));
            }

            var newComponent = InstantiateComponent(frame.ComponentType);
            var newComponentState = AttachAndInitComponent(newComponent, parentComponentId);
            frame = frame.WithComponent(newComponentState);
        }

        internal void AddToPendingTasks(int componentId, IComponent component, Task task)
        {
            switch (task == null ? TaskStatus.RanToCompletion : task.Status)
            {
                // If it's already completed synchronously, no need to add it to the list of
                // pending Tasks as no further render (we already rerender synchronously) will.
                // happen.
                case TaskStatus.RanToCompletion:
                case TaskStatus.Canceled:
                    break;
                case TaskStatus.Faulted:
                    if (!HandleException(componentId, component, task.Exception.GetBaseException()))
                    {
                    // We want to throw immediately if the task failed synchronously instead of
                    // waiting for it to throw later. This can happen if the task is produced by
                    // an 'async' state machine (the ones generated using async/await) where even
                    // the synchronous exceptions will get captured and converted into a faulted
                    // task.
                        ExceptionDispatchInfo.Capture(task.Exception.GetBaseException()).Throw();
                    }
                    break;
                default:
                    // We are not in rendering the root component.
                    if (_pendingTasks == null)
                    {
                        return;
                    }
                    _pendingTasks.Add(task);
                    break;
            }
        }

        internal void AssignEventHandlerId(ref RenderTreeFrame frame)
        {
            var id = ++_lastEventHandlerId;

            if (frame.AttributeValue is MulticastDelegate @delegate)
            {
                _eventBindings.Add(id, new EventHandlerInvoker(@delegate));
            }

            frame = frame.WithAttributeEventHandlerId(id);
        }

        /// <summary>
        /// Schedules a render for the specified <paramref name="componentId"/>. Its display
        /// will be populated using the specified <paramref name="renderFragment"/>.
        /// </summary>
        /// <param name="componentId">The ID of the component to render.</param>
        /// <param name="renderFragment">A <see cref="RenderFragment"/> that will supply the updated UI contents.</param>
        protected internal virtual void AddToRenderQueue(int componentId, RenderFragment renderFragment)
        {
            EnsureSynchronizationContext();

            var componentState = GetOptionalComponentState(componentId);
            if (componentState == null)
            {
                // If the component was already disposed, then its render handle trying to
                // queue a render is a no-op.
                return;
            }

            _batchBuilder.ComponentRenderQueue.Enqueue(
                new RenderQueueEntry(componentState, renderFragment));

            if (!_isBatchInProgress)
            {
                ProcessRenderQueue();
            }
        }

        private void EnsureSynchronizationContext()
        {
            // When the IDispatcher is a synchronization context
            // Render operations are not thread-safe, so they need to be serialized.
            // Plus, any other logic that mutates state accessed during rendering also
            // needs not to run concurrently with rendering so should be dispatched to
            // the renderer's sync context.
            if (_dispatcher is SynchronizationContext synchronizationContext && SynchronizationContext.Current != synchronizationContext)
            {
                throw new InvalidOperationException(
                    "The current thread is not associated with the renderer's synchronization context. " +
                    "Use Invoke() or InvokeAsync() to switch execution to the renderer's synchronization " +
                    "context when triggering rendering or modifying any state accessed during rendering.");
            }
        }

        private ComponentState GetRequiredComponentState(int componentId)
            => _componentStateById.TryGetValue(componentId, out var componentState)
                ? componentState
                : throw new ArgumentException($"The renderer does not have a component with ID {componentId}.");

        private ComponentState GetOptionalComponentState(int componentId)
            => _componentStateById.TryGetValue(componentId, out var componentState)
                ? componentState
                : null;

        private void ProcessRenderQueue()
        {
            _isBatchInProgress = true;
            var updateDisplayTask = Task.CompletedTask;

            try
            {
                // Process render queue until empty
                while (_batchBuilder.ComponentRenderQueue.Count > 0)
                {
                    var nextToRender = _batchBuilder.ComponentRenderQueue.Dequeue();
                    RenderInExistingBatch(nextToRender);
                }

                var batch = _batchBuilder.ToBatch();
                updateDisplayTask = UpdateDisplayAsync(batch);
                InvokeRenderCompletedCalls(batch.UpdatedComponents);
            }
            finally
            {
                RemoveEventHandlerIds(_batchBuilder.DisposedEventHandlerIds.ToRange(), updateDisplayTask);
                _batchBuilder.Clear();
                _isBatchInProgress = false;
            }
        }

        private async void InvokeRenderCompletedCalls(ArrayRange<RenderTreeDiff> updatedComponents)
        {
            List<Task> batch = null;
            var array = updatedComponents.Array;
            for (var i = 0; i < updatedComponents.Count; i++)
            {
                var componentState = GetOptionalComponentState(array[i].ComponentId);
                if (componentState != null)
                {
                // The component might be rendered and disposed in the same batch (if its parent
                // was rendered later in the batch, and removed the child from the tree).
                    batch = batch ?? new List<Task>();
                    batch.Add(NotifyRenderCompletedAsync(componentState));
            }
        }

            if (batch != null)
            {
                await Task.WhenAll(batch);
            }

            async Task NotifyRenderCompletedAsync(ComponentState componentState)
            {
                Task task = null;
                try
                {
                    task = componentState.NotifyRenderCompletedAsync();
                    await task;
                }
                catch (Exception ex) when (task.IsCanceled || HandleException(componentState.ComponentId, componentState.Component, ex))
                {
                    // Exception was a result of a canceled task or was handled
                }
            }
        }

        private void RenderInExistingBatch(RenderQueueEntry renderQueueEntry)
        {
            renderQueueEntry.ComponentState
                .RenderIntoBatch(_batchBuilder, renderQueueEntry.RenderFragment);

            // Process disposal queue now in case it causes further component renders to be enqueued
            while (_batchBuilder.ComponentDisposalQueue.Count > 0)
            {
                var disposeComponentId = _batchBuilder.ComponentDisposalQueue.Dequeue();
                GetRequiredComponentState(disposeComponentId).DisposeInBatch(_batchBuilder);
                _componentStateById.Remove(disposeComponentId);
                _batchBuilder.DisposedComponentIds.Append(disposeComponentId);
            }
        }

        private void RemoveEventHandlerIds(ArrayRange<int> eventHandlerIds, Task afterTask)
        {
            if (eventHandlerIds.Count == 0)
            {
                return;
            }

            if (afterTask.IsCompleted)
            {
                var array = eventHandlerIds.Array;
                var count = eventHandlerIds.Count;
                for (var i = 0; i < count; i++)
                {
                    _eventBindings.Remove(array[i]);
                }
            }
            else
            {
                // We need to delay the actual removal (e.g., until we've confirmed the client
                // has processed the batch and hence can be sure not to reuse the handler IDs
                // any further). We must clone the data because the underlying RenderBatchBuilder
                // may be reused and hence modified by an unrelated subsequent batch.
                var eventHandlerIdsClone = eventHandlerIds.Clone();
                afterTask.ContinueWith(_ =>
                    RemoveEventHandlerIds(eventHandlerIdsClone, Task.CompletedTask));
            }
        }

        /// <summary>
        /// Releases all resources currently used by this <see cref="Renderer"/> instance.
        /// </summary>
        /// <param name="disposing"><see langword="true"/> if this method is being invoked by <see cref="IDisposable.Dispose"/>, otherwise <see langword="false"/>.</param>
        protected virtual void Dispose(bool disposing)
        {
            List<Exception> exceptions = null;

            foreach (var componentState in _componentStateById.Values)
            {
                if (componentState.Component is IDisposable disposable)
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch (Exception exception) when (!HandleException(componentState.ComponentId, componentState.Component, exception))
                    {
                        // Unhandled exception. Accumulate exceptions from all components and rethrow an aggregate.
                        // Capture exceptions thrown by individual components and rethrow as an aggregate.
                        exceptions = exceptions ?? new List<Exception>();
                        exceptions.Add(exception);
                    }
                }
            }

            if (exceptions != null)
            {
                throw new AggregateException(exceptions);
            }
        }

        /// <summary>
        /// Releases all resources currently used by this <see cref="Renderer"/> instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
        }
    }
}
