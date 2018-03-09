// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Benchmarks.ClientJob;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Internal;
using Microsoft.AspNetCore.SignalR.Internal.Encoders;
using Microsoft.AspNetCore.SignalR.Internal.Protocol;
using Microsoft.AspNetCore.Sockets;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BenchmarksWorkers.Workers
{
    public class SignalRWorker : IWorker
    {
        public string JobLogText { get; set; }

        private ClientJob _job;
        private HttpClientHandler _httpClientHandler;
        private List<ConnectionWrapper> _connections;
        private List<IDisposable> _recvCallbacks;
        private Timer _timer;
        private List<int> _requestsPerConnection;
        private List<List<double>> _latencyPerConnection;
        private Stopwatch _workTimer = new Stopwatch();
        private bool _stopped;
        private SemaphoreSlim _lock = new SemaphoreSlim(1);

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

            if (_job.ClientProperties.TryGetValue("TransportType", out var transport))
            {
                jobLogText += $" TransportType:{transport}";
            }

            jobLogText += "]";
            JobLogText = jobLogText;

            CreateConnections(transport);
        }

        public async Task StartAsync()
        {
            await StartConnections();
            _workTimer.Start();
            _timer = new Timer(StopClients, null, TimeSpan.FromSeconds(_job.Duration), Timeout.InfiniteTimeSpan);

            _job.State = ClientState.Running;
            _job.LastDriverCommunicationUtc = DateTime.UtcNow;
        }

        private async void StopClients(object t)
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
            if (!await _lock.WaitAsync(0))
            {
                // someone else is stopping, we only need to do it once
                return;
            }
            try
            {
                if (_timer != null)
                {
                    _timer?.Dispose();
                    _timer = null;

                    foreach (var callback in _recvCallbacks)
                    {
                        // stops stat collection from happening quicker than StopAsync
                        // and we can do all the calculations while close is occurring
                        callback.Dispose();
                    }

                    _workTimer.Stop();

                    _stopped = true;

                    // stop connections
                    Log("Stopping connections");
                    var tasks = new List<Task>(_connections.Count);
                    foreach (var connection in _connections)
                    {
                        tasks.Add(connection.StopAsync());
                    }

                    CalculateStatistics();

                    await Task.WhenAll(tasks);

                    // TODO: Remove when clients no longer take a long time to "cool down"
                    await Task.Delay(5000);

                    Log("Stopped worker");
                }
            }
            finally
            {
                _lock.Release();
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

        private void CreateConnections(string transport)
        {
            _connections = new List<ConnectionWrapper>(_job.Connections);
            _requestsPerConnection = new List<int>(_job.Connections);
            _latencyPerConnection = new List<List<double>>(_job.Connections);

            if (string.Equals(transport, "sockets"))
            {
                CreateSockets();
            }
            else
            {
                CreateHubConnections(transport);
            }
        }

        private void CalculateStatistics()
        {
            // RPS
            var totalRequests = 0;
            var min = int.MaxValue;
            var max = 0;
            for (var i = 0; i < _requestsPerConnection.Count; i++)
            {
                totalRequests += _requestsPerConnection[i];

                if (_requestsPerConnection[i] > max)
                {
                    max = _requestsPerConnection[i];
                }
                if (_requestsPerConnection[i] < min)
                {
                    min = _requestsPerConnection[i];
                }
            }
            // Review: This could be interesting information, see the gap between most active and least active connection
            // Ideally they should be within a couple percent of each other, but if they aren't something could be wrong
            Log($"Least Requests per Connection: {min}");
            Log($"Most Requests per Connection: {max}");

            var rps = (double)totalRequests / _workTimer.ElapsedMilliseconds * 1000;
            Log($"Total RPS: {rps}");
            _job.RequestsPerSecond = rps;

            // Latency
            CalculateLatency();
        }

        private void CalculateLatency()
        {
            var avg = new List<double>(_latencyPerConnection.Count);
            var totalAvg = 0.0;
            for (var i = 0; i < _latencyPerConnection.Count; i++)
            {
                avg.Add(0.0);
                for (var j = 0; j < _latencyPerConnection[i].Count; j++)
                {
                    avg[i] += _latencyPerConnection[i][j];
                }
                avg[i] /= _latencyPerConnection[i].Count;
                Log($"Average latency for connection #{i}: {avg[i]}");

                _latencyPerConnection[i].Sort();
                totalAvg += avg[i];
            }

            totalAvg /= avg.Count;
            _job.Latency.Average = totalAvg;

            var allConnections = new List<double>();
            foreach (var connectionLatency in _latencyPerConnection)
            {
                allConnections.AddRange(connectionLatency);
            }

            // Review: Each connection can have different latencies, how do we want to deal with that?
            // We could just combine them all and ignore the fact that they are different connections
            // Or we could preserve the results for each one and record them separately
            allConnections.Sort();
            _job.Latency.Within50thPercentile = GetPercentile(50, allConnections);
            _job.Latency.Within75thPercentile = GetPercentile(75, allConnections);
            _job.Latency.Within90thPercentile = GetPercentile(90, allConnections);
            _job.Latency.Within99thPercentile = GetPercentile(99, allConnections);
        }

        private double GetPercentile(int percent, List<double> sortedData)
        {
            var i = (percent * sortedData.Count) / 100.0 + 0.5;
            var fractionPart = i - Math.Truncate(i);

            return (1.0 - fractionPart) * sortedData[(int)Math.Truncate(i)] + fractionPart * sortedData[(int)Math.Ceiling(i)];
        }

        private static void Log(string message)
        {
            var time = DateTime.Now.ToString("hh:mm:ss.fff");
            Console.WriteLine($"[{time}] {message}");
        }

        private void CreateSockets()
        {
            for (var i = 0; i < _job.Connections; i++)
            {
                _connections.Add(new ConnectionWrapper(new ClientWebSocket(), _job));
            }
        }

        private void CreateHubConnections(string transport)
        {
            var hubConnectionBuilder = new HubConnectionBuilder()
                .WithUrl(_job.ServerBenchmarkUri)
                .WithMessageHandler(_httpClientHandler)
                .WithTransport(Enum.Parse<TransportType>(transport));

            if (_job.ClientProperties.TryGetValue("LogLevel", out var logLevel))
            {
                if (Enum.TryParse<LogLevel>(logLevel, ignoreCase: true, result: out var level))
                {
                    hubConnectionBuilder.WithConsoleLogger(level);
                }
            }

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
                _connections.Add(new ConnectionWrapper(connection));

                // Capture the connection ID
                var id = i;
                // setup event handlers
                _recvCallbacks.Add(connection.On<DateTime>("echo", utcNow =>
                {
                    // TODO: Collect all the things
                    _requestsPerConnection[id] += 1;

                    var latency = DateTime.UtcNow - utcNow;
                    _latencyPerConnection[id].Add(latency.TotalMilliseconds);
                }));

                connection.Closed += e =>
                {
                    if (!_stopped)
                    {
                        var error = $"Connection closed early: {e}";
                        _job.Error += error;
                        Log(error);
                    }
                };
            }
        }

        private async Task StartConnections()
        {
            // start connections
            var tasks = new List<Task>(_connections.Count);
            foreach (var connection in _connections)
            {
                tasks.Add(connection.StartAsync());
            }

            await Task.WhenAll(tasks);

            // SendAsync will return as soon as the request has been sent (non-blocking)
            await _connections[0].SendAsync("Echo", _job.Duration + 1);
        }

        private class ConnectionWrapper
        {
            private readonly HubConnection _hubConnection;
            private readonly ClientWebSocket _socketConnection;
            private readonly ClientJob _job;
            private long _count;

            private HubProtocolReaderWriter _hubProtocolReaderWriter;

            public ConnectionWrapper(HubConnection connection)
            {
                _hubConnection = connection;
            }

            public ConnectionWrapper(ClientWebSocket connection, ClientJob job)
            {
                _socketConnection = connection;
                _job = job;
            }

            public async Task StartAsync()
            {
                if (_hubConnection != null)
                {
                    await _hubConnection.StartAsync();
                }
                else
                {
                    _hubProtocolReaderWriter = new HubProtocolReaderWriter(new MessagePackHubProtocol(), new PassThroughEncoder());
                    await _socketConnection.ConnectAsync(new Uri(_job.ServerBenchmarkUri), default);
                    var protocol = "json";
                    _job.ClientProperties.TryGetValue("HubProtocol", out protocol);
                    var neg = $"{{\"protocol\": \"{protocol}\"}}";
                    await _socketConnection.SendAsync(Encoding.UTF8.GetBytes(neg), WebSocketMessageType.Binary, true, default);

                    // setup receive
                    _ = Task.Run(async () =>
                    {

                        while (_socketConnection.State != WebSocketState.Aborted &&
                            _socketConnection.State != WebSocketState.Closed &&
                            _socketConnection.State != WebSocketState.CloseReceived &&
                            _socketConnection.State != WebSocketState.CloseSent)
                        {
                            var buffer = new byte[4096];
                            var result = await _socketConnection.ReceiveAsync(buffer, default);

                            if (result.CloseStatus != null)
                            {
                                Log($"Connection closed early: {result.CloseStatus}");
                                return;
                            }

                            _count += result.Count;
                            //_hubProtocolReaderWriter.ReadMessages(buffer, )
                        }
                    });
                }
            }

            public Task SendAsync(string target, params object[] args)
            {
                if (_hubConnection != null)
                {
                    return _hubConnection.SendAsync(target, args);
                }
                else
                {
                    var bytes = _hubProtocolReaderWriter.WriteMessage(new InvocationMessage(target, null, args));
                    return _socketConnection.SendAsync(bytes, WebSocketMessageType.Binary, true, default);
                }
            }

            public Task StopAsync()
            {
                if (_hubConnection != null)
                {
                    return _hubConnection.StopAsync();
                }
                else
                {
                    Log($"Total bytes received: {_count}");
                    return _socketConnection.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, default);
                }
            }

            public async Task DisposeAsync()
            {
                if (_hubConnection != null)
                {
                    await _hubConnection.DisposeAsync();
                }
                else
                {
                    _socketConnection.Dispose();
                }
            }
        }
    }
}
