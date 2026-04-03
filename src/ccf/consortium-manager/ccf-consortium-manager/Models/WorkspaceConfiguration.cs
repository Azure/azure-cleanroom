// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections;

namespace CcfConsortiumMgr.Models;

public class WorkspaceConfiguration
{
    public required IDictionary EnvironmentVariables { get; set; }
}