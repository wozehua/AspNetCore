// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Components
{
    /// <summary>
    /// Interface implemented by components that receive notification of their events.
    /// </summary>
    public interface IHandleEvent
    {
        /// <summary>
        /// Notifies the component that one of its event handlers has been triggered.
        /// </summary>
        /// <param name="binding">The event binding.</param>
        /// <param name="args">Arguments for the event handler.</param>
        /// <returns>A <see cref="Task"/> that represents the asynchronous event handling operation.</returns>
        Task HandleEventAsync(EventHandlerInvoker binding, UIEventArgs args);
    }
}
