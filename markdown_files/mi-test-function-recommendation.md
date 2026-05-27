# Recommendation — a .NET 10 isolated-worker test function for VM Managed Identity

The goal is a minimal HTTP-triggered function you can run locally on an Azure VM with `func start` (or F5 in VS Code) that **proves** the VM's managed identity is picked up via IMDS by both `ManagedIdentityCredential` and `DefaultAzureCredential`, and optionally exercises a real RBAC-protected data-plane call (Key Vault / Storage).

The recommendation below follows the **isolated worker model** on **.NET 10**, which is the supported model going forward (the in-process model is retired on November 10, 2026) and which is the only model that lets you target .NET 10 today ([Guide for running C# Azure Functions in the isolated worker model — Supported versions](https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-process-guide#supported-versions)).

---

## 1. Prerequisites on the Azure VM

- .NET 10 SDK installed ([download](https://dotnet.microsoft.com/download)).
- Azure Functions Core Tools **v4.x** (Windows 64-bit MSI recommended). See [Install Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local#install-the-azure-functions-core-tools).
- System-assigned (or user-assigned) managed identity enabled on the VM, with at least one RBAC role on a target resource (see [Configure managed identities on a Windows VM](https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/qs-configure-portal-windows-vm)).
- Verify IMDS works *before* even starting the function ([IMDS troubleshooting](https://learn.microsoft.com/troubleshoot/azure/virtual-machines/windows/windows-vm-imds-connection#cause)):

```powershell
Invoke-RestMethod -Headers @{Metadata="true"} -NoProxy `
  -Uri "http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https://management.azure.com/"
```

If that returns a token, the function will too.

- Recommended: run `az logout` (and sign out of VS / VS Code Azure account) before the test so `DefaultAzureCredential` clearly falls through to `ManagedIdentityCredential` via IMDS instead of using your developer identity ([credential chains](https://learn.microsoft.com/dotnet/azure/sdk/authentication/credential-chains#defaultazurecredential-overview)).

---

## 2. Create the project

```powershell
mkdir MiVmTest; cd MiVmTest
func init . --worker-runtime dotnet-isolated --target-framework net10.0
func new --name WhoAmI --template "HTTP trigger" --authlevel anonymous
```

Reference: [func init / func new](https://learn.microsoft.com/azure/azure-functions/functions-run-local#create-a-function).

> Note for .NET 10: per the [official guidance](https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-process-guide#core-packages), .NET 10 requires `Microsoft.Azure.Functions.Worker` **2.50.0+** and `Microsoft.Azure.Functions.Worker.Sdk` **2.0.5+**. The templates may scaffold older 1.x versions — bump them.

### `MiVmTest.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>MiVmTest</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="2.50.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="2.0.5" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http" Version="3.2.0" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore" Version="2.0.0" />

    <!-- Azure Identity + sample data-plane SDKs -->
    <PackageReference Include="Azure.Identity" Version="1.13.1" />
    <PackageReference Include="Azure.Security.KeyVault.Secrets" Version="4.7.0" />
    <PackageReference Include="Azure.Storage.Blobs" Version="12.22.2" />
  </ItemGroup>
</Project>
```

### `Program.cs`

```csharp
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.Build().Run();
```

(Pattern from [Start-up and configuration](https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-process-guide#start-up-and-configuration).)

### `WhoAmI.cs`

```csharp
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace MiVmTest;

public class WhoAmI(ILogger<WhoAmI> logger)
{
    // GET /api/whoami?resource=https://management.azure.com/&clientId=<optional UAMI client id>&kvUri=<optional>&blobUri=<optional>
    [Function(nameof(WhoAmI))]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequest req,
        CancellationToken ct)
    {
        string resource = req.Query["resource"].FirstOrDefault() ?? "https://management.azure.com/";
        string? clientId = req.Query["clientId"].FirstOrDefault();
        string? kvUri    = req.Query["kvUri"].FirstOrDefault();
        string? blobUri  = req.Query["blobUri"].FirstOrDefault();

        var result = new Dictionary<string, object?>
        {
            ["host"]     = Environment.MachineName,
            ["resource"] = resource,
            ["clientId"] = clientId,
        };

        // 1) ManagedIdentityCredential — proves IMDS path works on the VM.
        try
        {
            TokenCredential mi = string.IsNullOrEmpty(clientId)
                ? new ManagedIdentityCredential()
                : new ManagedIdentityCredential(clientId);

            var tok = await mi.GetTokenAsync(
                new TokenRequestContext(new[] { resource.TrimEnd('/') + "/.default" }), ct);

            result["managedIdentity"] = DescribeToken(tok);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ManagedIdentityCredential failed");
            result["managedIdentity"] = new { error = ex.GetType().Name, message = ex.Message };
        }

        // 2) DefaultAzureCredential — shows what the SDK chain picks (dev creds vs. VM MI).
        try
        {
            var dac = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = clientId
            });
            var tok = await dac.GetTokenAsync(
                new TokenRequestContext(new[] { resource.TrimEnd('/') + "/.default" }), ct);

            result["defaultAzureCredential"] = DescribeToken(tok);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DefaultAzureCredential failed");
            result["defaultAzureCredential"] = new { error = ex.GetType().Name, message = ex.Message };
        }

        // 3) Optional: real Key Vault data-plane call (needs "Key Vault Secrets User" on the MI).
        if (!string.IsNullOrEmpty(kvUri))
        {
            try
            {
                var client = new SecretClient(new Uri(kvUri), new ManagedIdentityCredential(clientId));
                var names = new List<string>();
                await foreach (var p in client.GetPropertiesOfSecretsAsync(ct))
                {
                    names.Add(p.Name);
                    if (names.Count >= 5) break;
                }
                result["keyVault"] = new { uri = kvUri, sampleSecrets = names };
            }
            catch (Exception ex)
            {
                result["keyVault"] = new { error = ex.GetType().Name, message = ex.Message };
            }
        }

        // 4) Optional: real Blob data-plane call (needs "Storage Blob Data Reader" on the MI).
        if (!string.IsNullOrEmpty(blobUri))
        {
            try
            {
                var svc = new BlobServiceClient(new Uri(blobUri), new ManagedIdentityCredential(clientId));
                var containers = new List<string>();
                await foreach (var c in svc.GetBlobContainersAsync(cancellationToken: ct))
                {
                    containers.Add(c.Name);
                    if (containers.Count >= 5) break;
                }
                result["blob"] = new { uri = blobUri, sampleContainers = containers };
            }
            catch (Exception ex)
            {
                result["blob"] = new { error = ex.GetType().Name, message = ex.Message };
            }
        }

        return new OkObjectResult(JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object DescribeToken(AccessToken tok)
    {
        // Decode JWT payload — never log/return the raw token in production.
        var parts = tok.Token.Split('.');
        if (parts.Length < 2) return new { expiresOn = tok.ExpiresOn };

        string pad = parts[1].Replace('-', '+').Replace('_', '/');
        pad = pad.PadRight(pad.Length + (4 - pad.Length % 4) % 4, '=');
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(pad));
        using var doc = JsonDocument.Parse(json);

        string? Get(string n) => doc.RootElement.TryGetProperty(n, out var v) ? v.GetString() : null;

        return new
        {
            expiresOn = tok.ExpiresOn,
            tid       = Get("tid"),       // tenant
            oid       = Get("oid"),       // object id of the MI service principal
            appid     = Get("appid"),     // client id of the MI
            xms_mirid = Get("xms_mirid"), // full resource id of the identity (VM or UAMI)
            aud       = Get("aud"),
            iss       = Get("iss"),
        };
    }
}
```

Why this design works as a test:

- The `xms_mirid` claim in the returned JWT contains the **resource ID of the identity that issued the token**, so you can prove unambiguously whether the token came from the VM's system-assigned MI, a user-assigned MI, or a developer credential. See [Managed identities on Azure VMs (IMDS)](https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/how-managed-identities-work-vm) and [ManagedIdentityCredential](https://learn.microsoft.com/dotnet/api/azure.identity.managedidentitycredential).
- Running both `ManagedIdentityCredential` and `DefaultAzureCredential` side-by-side makes the credential chain behavior visible — useful because `DefaultAzureCredential` prefers signed-in dev tools *before* IMDS ([credential chains](https://learn.microsoft.com/dotnet/azure/sdk/authentication/credential-chains#defaultazurecredential-overview)).
- Optional Key Vault / Blob calls validate **end-to-end RBAC**, not just token acquisition.

### `local.settings.json`

Keep it minimal — do **not** set `__credential=managedidentity` locally for trigger connections; that flag is for cloud only ([Functions developer guide — connections](https://learn.microsoft.com/azure/azure-functions/functions-reference#connections)):

```json
{
  "IsEncrypted": false,
  "Values": {
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "AzureWebJobsStorage": "UseDevelopmentStorage=true"
  }
}
```

(Use Azurite if you want the host to start without a real storage account; for a pure-HTTP test it isn't strictly required.)

---

## 2.1 Managed Identity code snippets only

The snippets below are extracted from `WhoAmI.cs` and show **only the lines that explicitly tell the Azure SDK to authenticate with a Managed Identity**. Pass `clientId` to target a User-Assigned MI; omit it to use the System-Assigned MI of the host VM.

### Acquire a token directly via `ManagedIdentityCredential`

```csharp
using Azure.Core;
using Azure.Identity;

// System-assigned MI when clientId is null/empty, otherwise the specified UAMI.
TokenCredential mi = string.IsNullOrEmpty(clientId)
    ? new ManagedIdentityCredential()
    : new ManagedIdentityCredential(clientId);

AccessToken tok = await mi.GetTokenAsync(
    new TokenRequestContext(new[] { resource.TrimEnd('/') + "/.default" }),
    ct);
```

### Force `DefaultAzureCredential` to prefer a specific UAMI

```csharp
using Azure.Identity;

var dac = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ManagedIdentityClientId = clientId // null => system-assigned MI
});
```

### Use Managed Identity with the Key Vault SDK

```csharp
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

var client = new SecretClient(
    new Uri(kvUri),
    new ManagedIdentityCredential(clientId)); // clientId optional
```

### Use Managed Identity with the Blob Storage SDK

```csharp
using Azure.Identity;
using Azure.Storage.Blobs;

var svc = new BlobServiceClient(
    new Uri(blobUri),
    new ManagedIdentityCredential(clientId)); // clientId optional
```

---

## 3. Run and call

```powershell
func start
```

Then from the VM:

```powershell
# System-assigned MI, ARM audience
curl "http://localhost:7071/api/whoami"

# User-assigned MI
curl "http://localhost:7071/api/whoami?clientId=<uami-client-id>"

# Real data-plane probes
curl "http://localhost:7071/api/whoami?kvUri=https://<kv-name>.vault.azure.net/"
curl "http://localhost:7071/api/whoami?blobUri=https://<storage>.blob.core.windows.net"
```

What "success" looks like:
- `managedIdentity.xms_mirid` ends with `/Microsoft.Compute/virtualMachines/<vmName>` (system-assigned) or `/Microsoft.ManagedIdentity/userAssignedIdentities/<name>` (user-assigned).
- `defaultAzureCredential.xms_mirid` matches the MI when no dev creds are signed in; otherwise it will reflect your dev identity — that's the expected and documented behavior of the chain.

---

## 4. Caveats worth keeping in mind

- **RBAC propagation lag**: after granting a role, restart `func start` if the first call returns 403 — token caches and AAD propagation can take a few minutes.
- **Proxies**: `169.254.169.254` must bypass any HTTP proxy on the VM, otherwise the SDK will time out.
- **Don't log the raw token** in any real scenario — the sample only decodes the (non-secret) JWT claims for diagnostics.
- This test covers your **application code path** only. Trigger/binding identity-based connections behave differently locally, exactly as your existing note describes.
