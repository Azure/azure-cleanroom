// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics.Metrics;

namespace Metrics;

internal class MeterMetricsEmitter : IMetricsEmitter
{
    private readonly Meter meter;
    private readonly Dictionary<string, Histogram<long>> histograms;

    public MeterMetricsEmitter(
        string meterName,
        Func<Dictionary<string, List<string>>> getMetricsToCreate)
    {
        this.meter = new(meterName, "1.0");
        Dictionary<string, List<string>> metricsToCreate = getMetricsToCreate();
        this.histograms = new();
        foreach (var metric in metricsToCreate)
        {
            this.histograms[metric.Key] = this.meter.CreateHistogram<long>(metric.Key);
        }
    }

    public void Log(string metricName, OrderedDictionary dimensions, OrderedDictionary annotations)
    {
        this.Log(metricName, 1, dimensions, annotations);
    }

    public void Log(
        string metricName,
        long logValue,
        OrderedDictionary dimensions,
        OrderedDictionary annotations)
    {
        if (!this.histograms.TryGetValue(metricName, out var histogram))
        {
            return;
        }

        if (dimensions != null)
        {
            var dims = new List<KeyValuePair<string, object?>>(dimensions.Count);
            foreach (DictionaryEntry entry in dimensions)
            {
                dims.Add(new((string)entry.Key, entry.Value ?? string.Empty));
            }

            histogram.Record(logValue, dims.ToArray());
        }
        else
        {
            histogram.Record(logValue);
        }
    }
}
