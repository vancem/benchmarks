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
        static bool Running;

        public override Task OnConnectedAsync()
        {
            return Task.CompletedTask;
        }

        public override Task OnDisconnectedAsync(Exception exception)
        {
            return Task.CompletedTask;
        }

        public async Task Echo(int duration)
        {
            Running = true;
            int i = 0;
            var t = new CancellationTokenSource();
            t.CancelAfter(TimeSpan.FromSeconds(duration));
            while (!t.IsCancellationRequested)
            {
                await Clients.All.SendAsync("echo", DateTime.UtcNow);
                i++;
                if (i % 100000 == 0)
                {
                    await Task.Delay(1);
                }
            }
            Console.WriteLine("Echo exited");
        }

        public Task Stop()
        {
            Running = false;
            Console.WriteLine("Stop called");
            return Task.CompletedTask;
        }
    }
}