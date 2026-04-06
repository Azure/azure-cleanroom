// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CleanRoomProvider;

#pragma warning disable SA1300 // Element should begin with upper-case letter
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InfraType
{
    /// <summary>
    /// Pods are started in AKS. Meant for production.
    /// </summary>
    aks,

    /// <summary>
    /// Pods are started in a kind cluster. Meant for local dev/test.
    /// </summary>
    @virtual
}
#pragma warning restore SA1300 // Element should begin with upper-case letter
