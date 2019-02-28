using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Prometheus.Client.Collectors.Abstractions;

namespace Prometheus.Client.Collectors
{
    public class CollectorRegistry : ICollectorRegistry, IDisposable
    {
        private static readonly Regex _metricNameRegex = new Regex("^[a-zA-Z_:][a-zA-Z0-9_:]*$", RegexOptions.Compiled);

        private readonly ReaderWriterLockSlim _lock;
        private readonly HashSet<string> _usedMetricNames;
        private readonly Dictionary<string, ICollector> _collectors;
        private Lazy<IEnumerable<ICollector>> _enumerableCollectors;

        public CollectorRegistry()
        {
            _lock = new ReaderWriterLockSlim();
            _usedMetricNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _collectors = new Dictionary<string, ICollector>();
            _enumerableCollectors = new Lazy<IEnumerable<ICollector>>(GetImmutableValueCollection);
        }

        public IEnumerable<ICollector> Enumerate() => _enumerableCollectors.Value;

        public void Add(string name, ICollector collector)
        {
            ValidateCollectorName(name);
            _lock.EnterWriteLock();
            try
            {
                AddInternal(name, collector);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public ICollector GetOrAdd(string name, Func<ICollector> collectorFactory)
        {
            ValidateCollectorName(name);
            _lock.EnterReadLock();
            try
            {
                if (_collectors.TryGetValue(name, out var collector))
                    return collector;
            }
            finally
            {
                _lock.ExitReadLock();
            }

            _lock.EnterWriteLock();
            try
            {
                if (_collectors.TryGetValue(name, out var collector))
                    return collector;

                collector = collectorFactory();
                AddInternal(name, collector);
                return collector;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public ICollector Remove(string name)
        {
            ValidateCollectorName(name);
            ICollector collector;
            _lock.EnterReadLock();
            try
            {
                if (!_collectors.TryGetValue(name, out collector))
                    return null;
            }
            finally
            {
                _lock.ExitReadLock();
            }

            _lock.EnterWriteLock();
            try
            {
                _collectors.Remove(name);
                _usedMetricNames.ExceptWith(collector.MetricNames);
                _enumerableCollectors = new Lazy<IEnumerable<ICollector>>(GetImmutableValueCollection);
                return collector;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private void ValidateCollectorName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));
        }

        private void AddInternal(string name, ICollector collector)
        {
            if (collector.MetricNames == null || collector.MetricNames.Length == 0)
                throw new ArgumentNullException(nameof(collector.MetricNames), "Collector should define metric names");

            if (_collectors.ContainsKey(name))
                throw new ArgumentException($"Collector with name '{name}' is already registered");

            for (var i = 0; i < collector.MetricNames.Length; i++)
            {
                var metricName = collector.MetricNames[i];
                if (!_metricNameRegex.IsMatch(metricName))
                    throw new ArgumentException($"Metric name '{metricName}' does not match metric name restriction");

                if (_usedMetricNames.Contains(metricName))
                    throw new ArgumentException($"Metric name '{metricName}' is already in use");
            }

            _collectors.Add(name, collector);
            _usedMetricNames.UnionWith(collector.MetricNames);
            _enumerableCollectors = new Lazy<IEnumerable<ICollector>>(GetImmutableValueCollection);
        }

        private IEnumerable<ICollector> GetImmutableValueCollection()
        {
            _lock.EnterReadLock();
            try
            {
                var collectors = new ICollector[_collectors.Count];
                _collectors.Values.CopyTo(collectors, 0);
                return collectors;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void Dispose()
        {
            _lock.Dispose();
        }
    }
}
