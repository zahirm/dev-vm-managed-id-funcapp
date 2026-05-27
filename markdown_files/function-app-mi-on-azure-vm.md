# Yes — it works, with one important nuance

When Azure Functions Core Tools (`func start`) runs your app in VS Code on an **Azure VM**, your function process can reach the VM's **Instance Metadata Service (IMDS)** at `http://169.254.169.254/metadata/identity/oauth2/token`, which is exactly the endpoint `ManagedIdentityCredential` uses. So any Azure SDK call from **your function code** using `DefaultAzureCredential` or `ManagedIdentityCredential` will get a token from the VM's managed identity ([How managed identities work with Azure VMs](https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/how-managed-identities-work-vm), [ManagedIdentityCredential class](https://learn.microsoft.com/javascript/api/@azure/identity/managedidentitycredential?view=azure-node-latest)).

The nuance: this only applies to **your application code**. For Functions **triggers and bindings** that use identity-based connections (e.g. `AzureWebJobsStorage__accountName`, `ServiceBusConnection__fullyQualifiedNamespace`), the Functions host uses your *developer identity* locally — not the VM's MI — and the `__credential=managedidentity` setting "shouldn't be set in local development scenarios" per the [Functions developer guide](https://learn.microsoft.com/azure/azure-functions/functions-reference#connections). Locally those still resolve through `DefaultAzureCredential`'s chain (which, on an Azure VM, will fall through to the VM's MI if no developer credentials are signed in).

## Steps

1. **Assign a managed identity to the Azure VM**
   - Portal: VM → *Identity* → *System assigned* → **On** (or attach a user-assigned identity). See [Configure managed identities on a VM](https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/qs-configure-portal-windows-vm).

2. **Grant that identity RBAC on the target resources** (Key Vault, Storage, Service Bus, SQL, etc.) using least privilege — e.g. *Storage Blob Data Reader*, *Key Vault Secrets User* ([Assign Azure roles](https://learn.microsoft.com/azure/role-based-access-control/role-assignments-steps)).

3. **Verify IMDS is reachable from inside the VM** (it should be by default; corporate proxies or firewalls sometimes block it):
   ```powershell
   Invoke-RestMethod -Headers @{Metadata="true"} -NoProxy `
     -Uri "http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https://management.azure.com/"
   ```
   See [IMDS connection issues troubleshooting](https://learn.microsoft.com/troubleshoot/azure/virtual-machines/windows/windows-vm-imds-connection#cause).

4. **In your function code, use `DefaultAzureCredential`** from `Azure.Identity` / `@azure/identity` / `azure-identity`. Example (C#):
   ```csharp
   var client = new SecretClient(
       new Uri("https://<kv-name>.vault.azure.net/"),
       new DefaultAzureCredential());
   ```
   On the VM, the chain will resolve to the VM's MI via IMDS. For a **user-assigned** MI, pass its client ID so the right identity is selected:
   ```csharp
   new DefaultAzureCredential(new DefaultAzureCredentialOptions {
       ManagedIdentityClientId = "<user-assigned-mi-client-id>"
   });
   ```
   Reference: [Authenticate .NET apps with system-assigned MI](https://learn.microsoft.com/dotnet/azure/sdk/authentication/system-assigned-managed-identity) and [user-assigned MI](https://learn.microsoft.com/dotnet/azure/sdk/authentication/user-assigned-managed-identity).

5. **Avoid signed-in dev credentials interfering.** `DefaultAzureCredential` tries Azure CLI / VS / VS Code creds *before* MI. If you've run `az login` on the VM, those tokens are used instead — usually fine, but if you specifically want the VM MI path, either sign out (`az logout`) or use `ManagedIdentityCredential` directly:
   ```csharp
   var cred = new ManagedIdentityCredential(); // system-assigned
   // or: new ManagedIdentityCredential("<client-id>"); // user-assigned
   ```

6. **For trigger/binding identity-based connections used locally** (e.g. Blob/Queue/Service Bus triggers), in `local.settings.json` set only the `__fullyQualifiedNamespace` / `__serviceUri` / `__accountName` form — **do not** set `__credential=managedidentity` locally — and assign the data-plane role to your developer identity (or to the VM MI if no dev login is present). See [Local development with identity-based connections](https://learn.microsoft.com/azure/azure-functions/functions-reference#local-development-with-identity-based-connections).

7. **Run normally** with `func start` (or F5 in VS Code). Tokens will be acquired from IMDS transparently.

## Caveats

- Token RBAC changes may require restarting the function host to pick up new permissions ([alt token retrieval](https://learn.microsoft.com/azure/operator-nexus/troubleshoot-virtual-machines-arc-enroll-with-managed-identities#alternative-access-token-retrieval-methods)).
- Azure Files (used by Consumption/Elastic Premium for content share) does **not** support MI; this only matters in the cloud, not local dev ([Functions connections guide](https://learn.microsoft.com/azure/azure-functions/functions-reference#connections)).
- If your VM is behind a proxy, ensure `169.254.169.254` bypasses the proxy or the SDK call will time out.
