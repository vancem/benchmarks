// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkClient;
using Benchmarks.ClientJob;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Sockets;
using Newtonsoft.Json;

namespace BenchmarksClient.Workers
{
    public class SignalRWorker : IWorker
    {
        public string JobLogText { get; set; }

        private ClientJob _job;
        private HttpClientHandler _httpClientHandler;
        private List<HubConnection> _connections;
        private List<IDisposable> _recvCallbacks;
        private Timer _timer;
        private List<int> _requestsPerConnection;
        private List<List<double>> _latencyPerConnection;
        private Stopwatch _workTimer = new Stopwatch();
        private bool _stopped;

        public SignalRWorker(ClientJob job)
        {
            _job = job;

            Debug.Assert(_job.Connections > 0, "There must be more than 0 connections");

            // Configuring the http client to trust the self-signed certificate
            _httpClientHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            var jobLogText =
                $"[ID:{_job.Id} Connections:{_job.Connections} Duration:{_job.Duration} Method:{_job.Method} ServerUrl:{_job.ServerBenchmarkUri}";

            if (_job.Headers != null)
            {
                jobLogText += $" Headers:{JsonConvert.SerializeObject(_job.Headers)}";
            }

            TransportType transportType = default;
            if (_job.ClientProperties.TryGetValue("TransportType", out var transport))
            {
                transportType = Enum.Parse<TransportType>(transport);
                jobLogText += $" TransportType:{transportType}";
            }

            jobLogText += "]";
            JobLogText = jobLogText;

            CreateConnections(transportType);
        }

        public async Task StartAsync()
        {
            // start connections
            var tasks = new List<Task>(_connections.Count);
            foreach (var connection in _connections)
            {
                tasks.Add(connection.StartAsync());
            }

            await Task.WhenAll(tasks);

            _job.State = ClientState.Running;

            // SendAsync will return as soon as the request has been sent (non-blocking)
            await _connections[0].SendAsync("Echo", _job.Duration + 1);
            _workTimer.Start();
            _timer = new Timer(tt, null, TimeSpan.FromSeconds(_job.Duration), Timeout.InfiniteTimeSpan);
        }

        private async void tt(object t)
        {
            try
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                await StopAsync();
            }
            finally
            {
                _job.State = ClientState.Completed;
            }
        }

        public async Task StopAsync()
        {
            if (_timer != null)
            {
                _timer?.Dispose();
                _timer = null;

                foreach (var callback in _recvCallbacks)
                {
                    callback.Dispose();
                }

                _workTimer.Stop();

                var totalRequests = 0;
                for (var i = 0; i < _requestsPerConnection.Count; i++)
                {
                    totalRequests += _requestsPerConnection[i];
                }

                var rps = (double)totalRequests / _workTimer.ElapsedMilliseconds * 1000;
                Startup.Log($"Total RPS: {rps}");
                _job.RequestsPerSecond = rps;
                _stopped = true;

                // stop connections
                Startup.Log("Stopping connections");
                var tasks = new List<Task>(_connections.Count);
                foreach (var connection in _connections)
                {
                    tasks.Add(connection.StopAsync());
                }

                await Task.WhenAll(tasks);

                await Task.Delay(5000);

                Startup.Log("Stopped worker");
            }
        }

        public void Dispose()
        {
            var tasks = new List<Task>(_connections.Count);
            foreach (var connection in _connections)
            {
                tasks.Add(connection.DisposeAsync());
            }

            Task.WhenAll(tasks).GetAwaiter().GetResult();

            _httpClientHandler.Dispose();
        }

        private void CreateConnections(TransportType transportType = TransportType.WebSockets)
        {
            _connections = new List<HubConnection>(_job.Connections);
            _requestsPerConnection = new List<int>(_job.Connections);
            _latencyPerConnection = new List<List<double>>(_job.Connections);

            var hubConnectionBuilder = new HubConnectionBuilder()
                .WithUrl(_job.ServerBenchmarkUri)
                .WithMessageHandler(_httpClientHandler)
                .WithTransport(transportType);

            if (_job.ClientProperties.TryGetValue("HubProtocol", out var protocolName))
            {
                switch (protocolName)
                {
                    case "messagepack":
                        hubConnectionBuilder.WithMessagePackProtocol();
                        break;
                    case "json":
                        hubConnectionBuilder.WithJsonProtocol();
                        break;
                    default:
                        throw new Exception($"{protocolName} is an invalid hub protocol name.");
                }
            }
            else
            {
                hubConnectionBuilder.WithJsonProtocol();
            }

            foreach (var header in _job.Headers)
            {
                hubConnectionBuilder.WithHeader(header.Key, header.Value);
            }

            _recvCallbacks = new List<IDisposable>(_job.Connections);
            for (var i = 0; i < _job.Connections; i++)
            {
                _requestsPerConnection.Add(0);
                _latencyPerConnection.Add(new List<double>());
                var connection = hubConnectionBuilder.Build();
                _connections.Add(connection);

                var id = i;
                // setup event handlers
                _recvCallbacks.Add(connection.On<DateTime>("echo", utcNow =>
                {
                    // TODO: Collect all the things

                    var latency = DateTime.UtcNow - utcNow;
                    if (latency.TotalMilliseconds > 500)
                    {
                        Startup.Log($"Latency was {latency.TotalMilliseconds}");
                    }
                }));

                connection.Closed += e =>
                {
                    if (!_stopped)
                    {
                        Startup.Log("Connection closed early");
                    }
                };
            }
        }
    }
}
