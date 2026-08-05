import "@azure-tools/typespec-client-generator-core";

// Import all model files
import "./models.tsp";

// Import API definitions
import "./app.tsp";

@service(#{ title: "Cleanroom Governance Service" })
@Azure.ClientGenerator.Core.clientNamespace("Azure.Cleanroom.Governance.Client")
namespace Azure.Cleanroom.Governance;
