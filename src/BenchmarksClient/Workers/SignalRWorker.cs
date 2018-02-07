using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
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

        public SignalRWorker(ClientJob job)
        {
            _job = job;

            // Configuring the http client to trust the self-signed certificate
            var httpClientHandler = new HttpClientHandler();
            httpClientHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

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

        public Task StartAsync()
        {
            // start connections

            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            // stop connections

            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        private void CreateConnections(TransportType transportType = TransportType.WebSockets)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl(_job.ServerBenchmarkUri)
                .WithTransport(transportType)
                .Build();
        }
    }
}
