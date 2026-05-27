# Adding Service Bus to the WhoAmI Managed Identity Test

This guide adds a **Service Bus** data-plane probe to the `WhoAmI` function, mirroring the existing Key Vault and Blob checks. After following these steps, you'll be able to call:

```
GET /api/whoami?clientId=<uami>&sbNamespace=<namespace>.servicebus.windows.net&sbQueue=<queue-or-topic>
```

and confirm that your Managed Identity can authenticate and send a message to Service Bus.

---

## 1. Add the Service Bus SDK package

From the project folder:

```powershell
Set-Location "c:\Users\winadmzams\Documents\retraite\MiVmTest"
dotnet add package Azure.Messaging.ServiceBus
```

Or edit `MiVmTest.csproj` and add to the existing `ItemGroup`:

```xml
<PackageReference Include="Azure.Messaging.ServiceBus" Version="7.18.2" />
```

---

## 2. Grant the Managed Identity an RBAC role on the namespace

The MI needs one of these built-in roles on the **Service Bus namespace** (or a specific queue/topic):

| Role | Purpose |
|---|---|
| **Azure Service Bus Data Sender** | Send messages only |
| **Azure Service Bus Data Receiver** | Receive/peek messages only |
| **Azure Service Bus Data Owner** | Full data-plane access (send + receive + manage) |

For this test (sending a probe message), grant **Azure Service Bus Data Sender**.

**Portal:** Service Bus namespace → Access control (IAM) → Add role assignment → pick the role → assign to your UAMI.

**CLI:**
```powershell
$miPrincipalId = (az identity show -g <rg> -n <uami-name> --query principalId -o tsv)
$sbScope = (az servicebus namespace show -g <rg> -n <namespace> --query id -o tsv)

az role assignment create `
  --assignee-object-id $miPrincipalId `
  --assignee-principal-type ServicePrincipal `
  --role "Azure Service Bus Data Sender" `
  --scope $sbScope
```

> Wait ~1 minute for RBAC propagation before testing.

---

## 3. Add the Service Bus probe to `WhoAmI.cs`

### 3a. Add the using

At the top of [WhoAmI.cs](../WhoAmI.cs), with the other `using` statements:

```csharp
using Azure.Messaging.ServiceBus;
```

### 3b. Read two new query parameters

Inside the `Run` method, next to the existing `kvUri` / `blobUri` lines:

```csharp
string? sbNamespace = req.Query["sbNamespace"].FirstOrDefault(); // e.g. mybus.servicebus.windows.net
string? sbQueue     = req.Query["sbQueue"].FirstOrDefault();     // queue or topic name
```

### 3c. Add the Service Bus block

Add this block after the existing **section 4 (Blob)** and **before** the final `return new OkObjectResult(...)`:

```csharp
// 5) Optional: real Service Bus data-plane call. Sends a tiny probe message to confirm
//    the MI can authenticate and is authorized on the namespace/queue/topic
//    (requires "Azure Service Bus Data Sender" on the MI for sending).
if (!string.IsNullOrEmpty(sbNamespace) && !string.IsNullOrEmpty(sbQueue))
{
    try
    {
        var credential = new ManagedIdentityCredential(clientId);

        await using var client = new ServiceBusClient(sbNamespace, credential);
        await using var sender = client.CreateSender(sbQueue);

        var message = new ServiceBusMessage($"whoami probe from {Environment.MachineName} at {DateTimeOffset.UtcNow:o}");
        await sender.SendMessageAsync(message, ct);

        result["serviceBus"] = new
        {
            ns = sbNamespace,
            entity = sbQueue,
            sent = true,
            messageId = message.MessageId
        };
    }
    catch (Exception ex)
    {
        // 401/403 here usually means the MI is missing the Data Sender role on the namespace.
        result["serviceBus"] = new { error = ex.GetType().Name, message = ex.Message };
    }
}
```

---

## 4. Build and run

```powershell
dotnet build
func start
```

---

## 5. Test with curl

System-assigned MI:
```powershell
curl.exe "http://localhost:7071/api/whoami?sbNamespace=<namespace>.servicebus.windows.net&sbQueue=<queue>"
```

User-assigned MI:
```powershell
curl.exe "http://localhost:7071/api/whoami?clientId=<uamiclientid>&sbNamespace=<namespace>.servicebus.windows.net&sbQueue=<queue>"
```

You can also combine probes:
```powershell
curl.exe "http://localhost:7071/api/whoami?clientId=<uamiclientid>&kvUri=https://kv-retz-test-01.vault.azure.net/&blobUri=https://storragtestz01.blob.core.windows.net/&sbNamespace=<namespace>.servicebus.windows.net&sbQueue=<queue>"
```

---

## 6. What the response means

**Success:**
```json
"serviceBus": {
  "ns": "mybus.servicebus.windows.net",
  "entity": "myqueue",
  "sent": true,
  "messageId": "..."
}
```
Then verify in the portal: Service Bus namespace → Queue → **Service Bus Explorer** → Peek to see the message.

**Failure — missing role (401/403):**
```json
"serviceBus": {
  "error": "ServiceBusException",
  "message": "Put token failed. status-code: 401, status-description: InvalidIssuer ..."
}
```
Fix: assign **Azure Service Bus Data Sender** to the MI on the namespace.

**Failure — wrong namespace/queue:**
```json
"serviceBus": {
  "error": "ServiceBusException",
  "message": "The messaging entity '...' could not be found."
}
```
Fix: check the namespace FQDN and queue/topic name.

---

## 7. Optional: also test receiving

If you want to test receive permissions too, add a separate probe using a `ServiceBusReceiver` and grant **Azure Service Bus Data Receiver**:

```csharp
await using var receiver = client.CreateReceiver(sbQueue);
var msg = await receiver.PeekMessageAsync(cancellationToken: ct);
result["serviceBusReceive"] = new { peeked = msg?.MessageId };
```
