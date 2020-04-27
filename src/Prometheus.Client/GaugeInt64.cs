using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Prometheus.Client.Abstractions;
using Prometheus.Client.MetricsWriter;
using Prometheus.Client.MetricsWriter.Abstractions;

namespace Prometheus.Client
{
    /// <inheritdoc cref="IGauge" />
    public sealed class GaugeInt64 : MetricBase<MetricConfiguration>, IGauge<long>
    {
        private ThreadSafeLong _value;

        internal GaugeInt64(MetricConfiguration configuration, IReadOnlyList<string> labels)
            : base(configuration, labels)
        {
        }

        public void Inc()
        {
            Inc(1, null);
        }

        public void Inc(long increment)
        {
            Inc(increment, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Inc(long increment, long? timestamp)
        {
            _value.Add(increment);
            TrackObservation(timestamp);
        }

        public void Set(long val)
        {
            Set(val, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(long val, long? timestamp)
        {
            _value.Value = val;
            TrackObservation(timestamp);
        }

        public void Dec()
        {
            Dec(1, null);
        }

        public void Dec(long decrement)
        {
            Dec(decrement, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dec(long decrement, long? timestamp)
        {
            _value.Add(-decrement);
            TrackObservation(timestamp);
        }

        public long Value => _value.Value;

        protected internal override void Collect(IMetricsWriter writer)
        {
            writer.WriteSample(Value, string.Empty, Labels, Timestamp);
        }
    }
}
