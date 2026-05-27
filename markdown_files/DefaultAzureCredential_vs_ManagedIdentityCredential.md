# DefaultAzureCredential vs ManagedIdentityCredential

## `ManagedIdentityCredential`

A **single, specific** credential type. It only authenticates via the **Azure Instance Metadata Service (IMDS)** or equivalent MI endpoint (App Service, Functions, Container Apps, AKS, Arc, etc.).

- ✅ Predictable — always uses MI, nothing else.
- ❌ Fails locally on your dev machine (no IMDS available).
- 🎯 Best for: **production code** where you know MI is the only valid auth path.

```csharp
new ManagedIdentityCredential();              // system-assigned
new ManagedIdentityCredential("<clientId>");  // user-assigned
```

---

## `DefaultAzureCredential`

A **chain of credentials** tried in order. Returns the first one that succeeds. Roughly:

1. `EnvironmentCredential` (service principal via env vars)
2. `WorkloadIdentityCredential` (AKS federated identity)
3. `ManagedIdentityCredential` ← included in the chain
4. `VisualStudioCredential`
5. `AzureCliCredential` (`az login`)
6. `AzurePowerShellCredential` (`Connect-AzAccount`)
7. `AzureDeveloperCliCredential` (`azd auth login`)

- ✅ "Just works" across dev laptop AND cloud — same code path.
- ❌ Hides what's actually being used; can pick a developer identity by accident.
- 🎯 Best for: **dev/test code** and apps that need to run in multiple environments.

```csharp
new DefaultAzureCredential();
new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ManagedIdentityClientId = "<uami-client-id>" // hint for the MI step
});
```

---

## Side-by-side Comparison

| | `ManagedIdentityCredential` | `DefaultAzureCredential` |
|---|---|---|
| Auth sources | MI only | MI + env vars + dev tools (chain) |
| Works on dev laptop | ❌ | ✅ (via CLI/VS) |
| Works on Azure VM/App | ✅ | ✅ |
| Predictable | ✅ | ❌ (depends on environment) |
| Startup latency | Fast | Slower (tries multiple sources) |
| Recommended for prod | ✅ | ⚠️ Acceptable but explicit MI is safer |

---

## Practical Guidance

**Prod-only services (e.g., your VM function):**
- Prefer `ManagedIdentityCredential`. You get clear, fast failures if MI is misconfigured instead of silently falling back to a dev credential.

**Apps that must run locally AND in Azure:**
- Use `DefaultAzureCredential`, but always set `ManagedIdentityClientId` if you use a UAMI, and `az logout` before testing MI behavior locally so it doesn't mask issues.

---

## Why the `WhoAmI` Function Uses Both

Your `WhoAmI` function runs both side-by-side precisely so you can compare which identity each one ends up using (via the `xms_mirid` claim in the returned JWT). This gives you immediate visibility into:

- Whether your MI is correctly configured on the host
- Whether `DefaultAzureCredential` is picking up an unintended dev credential
- Which identity type is actually being used (system-assigned vs. user-assigned)
