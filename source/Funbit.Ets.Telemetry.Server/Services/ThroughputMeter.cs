using System;
using System.Collections.Generic;

namespace Funbit.Ets.Telemetry.Server.Services
{
    public sealed class ThroughputMeter
    {
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

        struct Reading
        {
            public DateTime At;
            public long Sent, Received;
        }

        readonly Queue<Reading> _samples = new Queue<Reading>();

        public double UpBytesPerSecond { get; private set; }
        public double DownBytesPerSecond { get; private set; }

        public void Sample(long sent, long received, DateTime now)
        {
            _samples.Enqueue(new Reading { At = now, Sent = sent, Received = received });
            while (_samples.Count > 1 && now - _samples.Peek().At > Window)
                _samples.Dequeue();
            var oldest = _samples.Peek();
            double seconds = (now - oldest.At).TotalSeconds;
            UpBytesPerSecond = seconds > 0 ? (sent - oldest.Sent) / seconds : 0;
            DownBytesPerSecond = seconds > 0 ? (received - oldest.Received) / seconds : 0;
        }
    }
}
