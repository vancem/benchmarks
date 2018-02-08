using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
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
        public string JobLogText { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        private ClientJob _job;
        private HttpClientHandler _httpClientHandler;
        private List<HubConnection> _connections;
        private Task _sendTask;

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
                        $"[ID:{_job.Id} Connections:{_job.Connections} Threads:{_job.WorkerProperties["Threads"]} Duration:{_job.Duration} Method:{_job.Method} ServerUrl:{_job.ServerBenchmarkUri}";

            if (_job.Headers != null)
            {
                jobLogText += $" Headers:{JsonConvert.SerializeObject(_job.Headers)}";
            }

            TransportType transportType = default;
            if (_job.WorkerProperties.TryGetValue("TransportType", out var transport))
            {
                transportType = Enum.Parse<TransportType>((string)transport);
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

            _job.State = ClientState.Running;
            await Task.WhenAll(tasks);

            // This should last until StopAsync is called
            _sendTask = _connections[0].SendAsync("Echo");
        }

        public async Task StopAsync()
        {
            // stop connections
            var tasks = new List<Task>(_connections.Count);
            foreach (var connection in _connections)
            {
                tasks.Add(connection.StopAsync());
            }

            await Task.WhenAll(tasks);

            if (await Task.WhenAny(_sendTask, Task.Delay(1000)) != _sendTask)
            {
                Startup.Log("SendTask didn't finish in a reasonable time");
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

            var hubConnectionBuilder = new HubConnectionBuilder()
                .WithUrl(_job.ServerBenchmarkUri)
                .WithMessageHandler(_httpClientHandler)
                .WithTransport(transportType);

            for (var i = 0; i < _job.Connections; i++)
            {
                var connection = hubConnectionBuilder.Build();
                _connections.Add(connection);

                // setup event handlers
                connection.On("echo", () =>
                {
                    // TODO: Collect all the things
                });
            }
        }
    }
}
