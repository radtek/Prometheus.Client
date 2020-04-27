using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Prometheus.Client.Abstractions;
using Prometheus.Client.MetricsWriter;
using Prometheus.Client.MetricsWriter.Abstractions;
using Prometheus.Client.SummaryImpl;

namespace Prometheus.Client
{
    /// <inheritdoc cref="ISummary" />
    public sealed class Summary : MetricBase<SummaryConfiguration>, ISummary
    {
        private static readonly ArrayPool<double> _arrayPool = ArrayPool<double>.Shared;
        // Protects hotBuf and hotBufExpTime.
        private readonly object _bufLock = new object();

        // Protects every other moving part.
        // Lock bufMtx before mtx if both are needed.
        private readonly object _lock = new object();

        private SampleBuffer _buffer;
        private DateTimeOffset _bufferExpTime;
        private long _count;
        private QuantileStream _headStream;
        private DateTimeOffset _headStreamExpTime;
        private int _headStreamIdx;

        private TimeSpan _streamDuration;
        private QuantileStream[] _streams;
        private double _sum;

        public Summary(SummaryConfiguration configuration, IReadOnlyList<string> labels, Func<DateTimeOffset> currentTimeProvider = null)
            : base(configuration, labels, currentTimeProvider)
        {
            _buffer = new SampleBuffer(Configuration.BufCap);
            _streamDuration = new TimeSpan(Configuration.MaxAge.Ticks / Configuration.AgeBuckets);
            _headStreamExpTime = GetUtcNow().Add(_streamDuration);
            _bufferExpTime = _headStreamExpTime;

            _streams = new QuantileStream[Configuration.AgeBuckets];
            for (int i = 0; i < Configuration.AgeBuckets; i++)
                _streams[i] = QuantileStream.NewTargeted(Configuration.Objectives);

            _headStream = _streams[0];
        }

        public SummaryState Value
        {
            get
            {
                var values = new double[Configuration.SortedObjectives.Count];
                ForkState(out var count, out var sum, values);
                var zipped = values.Zip(Configuration.SortedObjectives, (v, k) => new KeyValuePair<double, double>(k, v)).ToArray();
                return new SummaryState(count, sum, zipped);
            }
        }

        public void Observe(double val)
        {
            Observe(val, null);
        }

        public void Observe(double val, long? timestamp)
        {
            var now = GetUtcNow();
            lock (_bufLock)
            {
                if (now > _bufferExpTime)
                    Flush();

                _buffer.Append(val);

                if (_buffer.IsFull)
                    Flush();
            }

            TrackObservation(timestamp);
        }

        internal void ForkState(out long count, out double sum, double[] values)
        {
            lock (_bufLock)
            {
                lock (_lock)
                {
                    // FlushBuffer even if buffer is empty to set new bufferExpTime.
                    FlushBuffer();

                    for (int i = 0; i < Configuration.SortedObjectives.Count; i++)
                    {
                        double rank = Configuration.SortedObjectives[i];
                        double value = _headStream.Count == 0 ? double.NaN : _headStream.Query(rank);

                        values[i] = value;
                    }

                    count = _count;
                    sum = _sum;
                }
            }
        }

        protected internal override void Collect(IMetricsWriter writer)
        {
            var values = _arrayPool.Rent(Configuration.SortedObjectives.Count);

            try
            {
                ForkState(out var count, out var sum, values);

                for (int i = 0; i < Configuration.SortedObjectives.Count; i++)
                {
                    var bucketSample = writer.StartSample();
                    var labelWriter = bucketSample.StartLabels();
                    if (Labels != null)
                        labelWriter.WriteLabels(Labels);

                    labelWriter.WriteLabel("quantile", Configuration.FormattedObjectives[i]);
                    labelWriter.EndLabels();

                    bucketSample.WriteValue(values[i]);
                    if (Timestamp.HasValue)
                        bucketSample.WriteTimestamp(Timestamp.Value);

                    bucketSample.EndSample();
                }

                writer.WriteSample(sum, "_sum", Labels, Timestamp);
                writer.WriteSample(count, "_count", Labels, Timestamp);
            }
            finally
            {
                _arrayPool.Return(values);
            }
        }

        // Flush needs bufMtx locked.
        private void Flush()
        {
            lock (_lock)
            {
                FlushBuffer();
            }
        }

        // FlushBuffer needs mtx AND bufMtx locked.
        private void FlushBuffer()
        {
            var now = GetUtcNow();
            for (int bufIdx = 0; bufIdx < _buffer.Position; bufIdx++)
            {
                double value = _buffer[bufIdx];

                foreach (var quantileStream in _streams)
                    quantileStream.Insert(value);

                _count++;
                _sum += value;
            }

            _buffer.Reset();

            // buffer is now empty and gets new expiration set.
            while (now > _bufferExpTime)
                _bufferExpTime = _bufferExpTime.Add(_streamDuration);

            MaybeRotateStreams();
        }

        // MaybeRotateStreams needs mtx AND bufMtx locked.
        private void MaybeRotateStreams()
        {
            while (!_bufferExpTime.Equals(_headStreamExpTime))
            {
                _headStream.Reset();
                _headStreamIdx++;

                if (_headStreamIdx >= _streams.Length)
                    _headStreamIdx = 0;

                _headStream = _streams[_headStreamIdx];
                _headStreamExpTime = _headStreamExpTime.Add(_streamDuration);
            }
        }
    }
}
