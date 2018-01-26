// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;

namespace BenchmarksClient.Workers
{
    public interface IWorker : IDisposable
    {
        string JobLogText { get; set; }

        void Start();
        void Stop();
    }
}
