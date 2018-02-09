// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading;
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
        static int ConnectionCount;

        public override Task OnConnectedAsync()
        {
            Interlocked.Increment(ref ConnectionCount);
            return Task.CompletedTask;
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            Console.WriteLine("OnDisconnected called");
            Interlocked.Decrement(ref ConnectionCount);
            return Task.CompletedTask;
        }

        public async Task Echo(long timestamp)
        {
            while (ConnectionCount > 0)
            {
                await Clients.All.SendAsync("echo", DateTime.UtcNow);
            }
            Console.WriteLine("Echo exited");
        }
    }
}