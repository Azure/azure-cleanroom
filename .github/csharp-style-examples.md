<!-- Referenced by instructions/csharp.instructions.md — C# code style examples -->

## Line-width examples

Bad — unnecessary break at ~55 chars:

```csharp
this.logger.LogInformation(
    "Processing request for id: {Id}",
    contractId);
```

Good — single line fits within 101 chars:

```csharp
this.logger.LogInformation("Processing request for id: {Id}", contractId);
```

Bad — each argument on its own line when everything fits on one:

```csharp
builder.UseSetting(
    "CCF_ENDPOINT",
    CcfEndpoint);
```

Good — key-value pair fits on one line:

```csharp
builder.UseSetting("CCF_ENDPOINT", CcfEndpoint);
```

Bad — property getter broken across lines for no reason:

```csharp
public string Endpoint =>
    $"http://localhost:{this.Port}/v1";
```

Good — expression fits on one line (65 chars):

```csharp
public string Endpoint => $"http://localhost:{this.Port}/v1";
```

Bad — chained methods broken aggressively at ~50 chars:

```csharp
var state = json.RootElement
    .GetProperty("status")
    .GetString();
```

Good — full chain fits on one line:

```csharp
string? state = json.RootElement.GetProperty("status").GetString();
```

OK — line must break because it exceeds 101 chars. Break at `.` boundary:

```csharp
var client = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetEmbeddingClient(model).AsIEmbeddingGenerator();
```

Bad — assert args each on a separate line when they fit on fewer:

```csharp
Assert.AreNotEqual(
    "Failed",
    state,
    "Operation should not fail.");
```

Good — all args fit on one line:

```csharp
Assert.AreNotEqual("Failed", state, "Operation should not fail.");
```

Bad — DI generic registration broken across lines at ~38 chars:

```csharp
services.AddSingleton<ISecretsClient,
    SecretsClient>();
```

Good — fits on one line:

```csharp
services.AddSingleton<ISecretsClient, SecretsClient>();
```

Bad — constructor base call broken at ~15 chars:

```csharp
public Startup(IConfiguration config)
    : base(
        config,
        Assembly.GetExecutingAssembly().GetName().Name!)
```

Good — base args fit on one line:

```csharp
public Startup(IConfiguration config)
    : base(config, Assembly.GetExecutingAssembly().GetName().Name!)
```

Bad — lambda DI registration broken into 7 lines:

```csharp
services.AddSingleton<IAttestationClient>(sp =>
    new AttestationClient(
        endpoint,
        credential,
        sp.GetRequiredService<
            ILogger<AttestationClient>>()));
```

Good — fits on 2 lines (under 101 chars each):

```csharp
services.AddSingleton<IAttestationClient>(sp => new AttestationClient(
    endpoint, credential, sp.GetRequiredService<ILogger<AttestationClient>>()));
```

Bad — string literals split into tiny fragments:

```csharp
string instructions =
    "You are a governance assistant. "
    + "Use the QueryContracts tool "
    + "to find relevant contracts "
    + "before answering questions "
    + "about specific topics.";
```

Good — combine fragments to fill lines up to 101 chars:

```csharp
string instructions = "You are a governance assistant. "
    + "Use the QueryContracts tool to find relevant contracts "
    + "before answering questions about specific topics.";
```

## `var` vs explicit type examples

Good — type is obvious from `new`:

```csharp
var options = new HttpClientTransportOptions();
var results = new List<ContractResult>();
```

Good — type is obvious from generic factory:

```csharp
var logger = LoggerFactory.Create<GovernanceService>();
```

Good — tuple deconstruction always uses `var`:

```csharp
var (chunk, hash) = chunks[i];
```

Good — `Enum.Parse<T>()` return type is clear from type argument:

```csharp
var state = Enum.Parse<ContractState>(contract.Status);
```

Bad — return type of `ReadFromJsonAsync` is not obvious:

```csharp
var identityToken = await response.Content.ReadFromJsonAsync<JsonObject>();
```

Good — explicit type makes the code self-documenting:

```csharp
JsonObject? identityToken = await response.Content.ReadFromJsonAsync<JsonObject>();
```

Bad — `GetProperty(...).GetString()` could return `string` or `string?`:

```csharp
var state = json.RootElement.GetProperty("status").GetString();
```

Good:

```csharp
string? state = json.RootElement.GetProperty("status").GetString();
```

Bad — config indexer return type is not obvious:

```csharp
var endpoint = this.config["CCF_ENDPOINT"] ?? "http://localhost:8080";
```

Good:

```csharp
string endpoint = this.config["CCF_ENDPOINT"] ?? "http://localhost:8080";
```

Bad — `string.Equals` returns `bool` but is not `new`/cast/literal/generic:

```csharp
var isVirtual = string.Equals(provider, "virtual", StringComparison.OrdinalIgnoreCase);
```

Good:

```csharp
bool isVirtual = string.Equals(provider, "virtual", StringComparison.OrdinalIgnoreCase);
```

## Argument-break examples

Bad — two arguments squeezed onto the continuation line:

```csharp
private async Task<ContractDetails?> FetchAndValidateContractAsync(
    ContractSummary summary, CancellationToken ct)
```

Good — each argument on its own line once a break is needed:

```csharp
private async Task<ContractDetails?> FetchAndValidateContractAsync(
    ContractSummary summary,
    CancellationToken ct)
```

## Positional record parameter examples

Bad — unnecessary break between attribute and parameter:

```csharp
internal sealed record ModelDocumentDto(
    [property: JsonPropertyName("name")]
    string Name,
    [property: JsonPropertyName("version")]
    string Version);
```

Good — attribute + parameter on one line (fits within 101 chars):

```csharp
internal sealed record ModelDocumentDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);
```
