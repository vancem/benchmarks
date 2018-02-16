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
        //private List<IDisposable> _recvCallbacks;
        private List<ClientWebSocket> _sockets;
        private Timer _timer;
        //private int req;
        private TaskCompletionSource<object> _completed;
        private List<int> _totalBytes;
        //private Task _echoTask;

        public SignalRWorker(ClientJob job)
        {
            _job = job;
            _completed = new TaskCompletionSource<object>();
            _totalBytes = new List<int>(_job.Connections);

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
            for (var i = 0; i < _job.Connections; ++i)
            {
                _totalBytes.Add(0);
            }
            //foreach (var connection in _connections)
            //{
            //    tasks.Add(connection.StartAsync());
            //}

            foreach (var socket in _sockets)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await socket.ConnectAsync(new Uri(_job.ServerBenchmarkUri.Replace("http", "ws")), CancellationToken.None);
                    var msg = "{\"protocol\":\"json\"}";
                    using (var strm = new MemoryStream())
                    {
                        WriteMessage(Encoding.UTF8.GetBytes(msg), strm);
                        await socket.SendAsync(strm.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            _job.State = ClientState.Running;

            var message = "{\"type\":1,\"invocationId\":\"1\",\"headers\":{},\"target\":\"Echo\",\"arguments\":[11]}";
            using (var strm = new MemoryStream())
            {
                WriteMessage(Encoding.UTF8.GetBytes(message), strm);
                await _sockets[0].SendAsync(strm.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
            }

            for (var i = 0; i < _job.Connections; ++i)
            {
                _ = Recv(i);
            }

            // SendAsync will return as soon as the request has been sent (non-blocking)
            //_echoTask = _connections[0].InvokeAsync<int>("Broadcast", _job.Duration + 1);
            _timer = new Timer(tt, null, TimeSpan.FromSeconds(_job.Duration), Timeout.InfiniteTimeSpan);
        }

        public static void WriteMessage(byte[] payload, Stream output)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(buffer, 0);
            output.Write(buffer, 0, payload.Length);
            output.WriteByte(0x1e);
        }

        private async Task Recv(int i)
        {
            var buf = new byte[2048];
            while (!_completed.Task.IsCompleted)
            {
                var res = await _sockets[i].ReceiveAsync(buf, CancellationToken.None);
                _totalBytes[i] += res.Count;
            }
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
                _completed.TrySetResult(null);
                //Startup.Log($"Bytes received: {}");
                //_job.RequestsPerSecond = (float)req / _job.Duration;
                Startup.Log(_job.RequestsPerSecond.ToString());

                _timer?.Dispose();
                _timer = null;

                var arr = new int[_job.Connections];
                _totalBytes.CopyTo(arr);
                foreach (var b in arr)
                {
                    Startup.Log(b.ToString());
                }

                //foreach (var callback in _recvCallbacks)
                //{
                //    callback.Dispose();
                //}

                //await _connections[0].InvokeAsync("Stop");
                //await _echoTask;

                // stop connections
                var tasks = new List<Task>(_connections.Count);
                //foreach (var connection in _connections)
                //{
                //    tasks.Add(connection.StopAsync());
                //}

                foreach (var socket in _sockets)
                {
                    tasks.Add(socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None));
                }

                await Task.WhenAll(tasks);

                await Task.Delay(1000);

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
            _sockets = new List<ClientWebSocket>(_job.Connections);

            for (var i = 0; i < _job.Connections; ++i)
            {
                _sockets.Add(new ClientWebSocket());
            }

        //    var hubConnectionBuilder = new HubConnectionBuilder()
        //        .WithUrl(_job.ServerBenchmarkUri)
        //        .WithMessageHandler(_httpClientHandler)
        //        .WithTransport(transportType);

        //    if (_job.ClientProperties.TryGetValue("HubProtocol", out var protocolName))
        //    {
        //        switch (protocolName)
        //        {
        //            case "messagepack":
        //                hubConnectionBuilder.WithMessagePackProtocol();
        //                break;
        //            case "json":
        //                hubConnectionBuilder.WithJsonProtocol();
        //                break;
        //            default:
        //                throw new Exception($"{protocolName} is an invalid hub protocol name.");
        //        }
        //    }
        //    else
        //    {
        //        hubConnectionBuilder.WithJsonProtocol();
        //    }

        //    foreach (var header in _job.Headers)
        //    {
        //        hubConnectionBuilder.WithHeader(header.Key, header.Value);
        //    }

        //    _recvCallbacks = new List<IDisposable>(_job.Connections);
        //    for (var i = 0; i < _job.Connections; i++)
        //    {
        //        var connection = hubConnectionBuilder.Build();
        //        _connections.Add(connection);

        //        // setup event handlers
        //        _recvCallbacks.Add(connection.On<DateTime>("echo", utcNow =>
        //        {
        //            // TODO: Collect all the things
        //            Interlocked.Increment(ref req);
        //            // DateTime.UtcNow - utcNow for latency
        //        }));
        //    }
        }
    }
}
