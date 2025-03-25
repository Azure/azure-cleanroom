// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CoseUtils;

namespace Controllers;

internal static class GovernanceCose
{
    public static Task<byte[]> CreateGovCoseSign1Message(
        CoseSignKey signKey,
        GovMessageType messageType,
        string? payload,
        string? proposalId = null)
    {
        return Cose.CreateGovCoseSign1Message(signKey, messageType, payload, proposalId);
    }
}
