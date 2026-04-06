// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using OpenTelemetry;

public sealed class BaggageSpanProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        foreach (var baggage in Baggage.GetBaggage())
        {
            activity.SetTag(baggage.Key, baggage.Value);
        }
    }
}