// CCF Governance API models and operations.
// Source: https://github.com/eddyashton/azure-rest-api-specs/tree/ccf_gov_2024_07_01
// Upstream namespace was Microsoft.ManagedCcf; renamed to Microsoft.Ccf.

import "@typespec/http";
import "@typespec/versioning";
import "@azure-tools/typespec-client-generator-core";

import "./gov/acks.tsp";
import "./gov/proposals.tsp";
import "./gov/recovery.tsp";
import "./gov/service_state.tsp";
import "./gov/transactions.tsp";

@service(#{ title: "CCF Governance" })
@TypeSpec.Versioning.versioned(Microsoft.Ccf.Versions)
@Azure.ClientGenerator.Core.clientNamespace("Microsoft.Ccf.Client")
namespace Microsoft.Ccf;

// Azure.Core v0.64.0 does not export a Versions enum, so useDependency is
// omitted.  The version value below must stay in sync with gov.json.
#suppress "@azure-tools/typespec-azure-core/documentation-required" "https://github.com/Azure/typespec-azure/issues/3107"
enum Versions {
  `2024-07-01`,
}
