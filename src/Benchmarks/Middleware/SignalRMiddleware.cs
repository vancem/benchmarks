// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Threading.Tasks;
using Benchmarks.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;

namespace Benchmarks.Middleware
{
    public static class SignalRMiddlewareExtensions
    {
        public static IApplicationBuilder UseSignalRMiddleware(this IApplicationBuilder builder)
        {
            return builder.UseSignalR(route =>
            {
                route.MapHub<EchoHub>(Scenarios.GetPath(s => s.SignalRBroadcast) + "/default");
            });
        }
    }

    public class EchoHub : Hub
    {
        public Task Echo(long timestamp)
        {
            return Clients.All.InvokeAsync("Echo", timestamp);
        }
    }
}
