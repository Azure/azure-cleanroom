Deploy the bicep file and then visit `<IPAddress>:9300/swagger` and invoke various endpoints.

```pwsh
az deployment group create --resource-group gsinhadev --template-file ./src/tools/attestation-report-generator/deploy.bicep
```

To generate new reports under `samples/reports` see [here](../../../samples/reports/README.md).