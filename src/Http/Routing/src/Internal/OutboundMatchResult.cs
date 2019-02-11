// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.AspNetCore.Routing.Tree;

namespace Microsoft.AspNetCore.Routing.Internal
{
    public readonly struct OutboundMatchResult
    {
        public OutboundMatchResult(OutboundMatch match, decimal quality)
        {
            Match = match;
            Quality = quality;
        }

        public OutboundMatch Match { get; }

        public decimal Quality { get; }
    }
}
