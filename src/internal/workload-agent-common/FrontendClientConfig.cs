// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public record FrontendClientConfig(
    string EndpointSettingName,
    string HostDataSettingName,
    string ServiceName);
