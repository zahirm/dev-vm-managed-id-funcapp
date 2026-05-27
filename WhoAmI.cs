using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace MiVmTest;

/// <summary>
/// Diagnostic HTTP-triggered Azure Function that verifies Managed Identity (MI)
/// behavior on the host (typically an Azure VM). It acquires tokens through
/// multiple credential paths and optionally performs real data-plane calls
/// against Key Vault and Blob Storage to confirm RBAC assignments.
/// </summary>
public class WhoAmI(ILogger<WhoAmI> logger)
{
    // Example: GET /api/whoami?resource=https://management.azure.com/&clientId=<optional UAMI client id>&kvUri=<optional>&blobUri=<optional>
    [Function(nameof(WhoAmI))]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequest req,
        CancellationToken ct)
    {
        // Read optional query string parameters that control which identity/resource to test.
        // - resource: AAD resource (audience) to request a token for. Defaults to ARM.
        // - clientId: client id of a User-Assigned Managed Identity (UAMI). Omit for System-Assigned MI.
        // - kvUri:    Key Vault URI to optionally exercise a data-plane call against.
        // - blobUri:  Blob service URI to optionally exercise a data-plane call against.
        string resource = req.Query["resource"].FirstOrDefault() ?? "https://management.azure.com/";
        string? clientId = req.Query["clientId"].FirstOrDefault();
        string? kvUri    = req.Query["kvUri"].FirstOrDefault();
        string? blobUri  = req.Query["blobUri"].FirstOrDefault();
        string? sbNamespace = req.Query["sbNamespace"].FirstOrDefault(); // e.g. mybus.servicebus.windows.net
        string? sbQueue     = req.Query["sbQueue"].FirstOrDefault();     // queue or topic name

        // Accumulator for the JSON response. Captures host info and per-check results.
        var result = new Dictionary<string, object?>
        {
            ["host"]     = Environment.MachineName,
            ["resource"] = resource,
            ["clientId"] = clientId,
        };

        // 1) ManagedIdentityCredential — directly hits the Azure Instance Metadata Service (IMDS)
        //    endpoint on the VM to get a token. This isolates the MI path from any other
        //    credential source and proves that the VM identity (system-assigned or the
        //    specified UAMI) is correctly configured and reachable.
        try
        {
            // Pick system-assigned MI when no clientId is provided, otherwise target a specific UAMI.
            TokenCredential mi = string.IsNullOrEmpty(clientId)
                ? new ManagedIdentityCredential()
                : new ManagedIdentityCredential(clientId);

            // Request a token for the given resource using the standard "/.default" scope.
            var tok = await mi.GetTokenAsync(
                new TokenRequestContext(new[] { resource.TrimEnd('/') + "/.default" }), ct);

            // Decode and return useful (non-sensitive) claims from the access token.
            result["managedIdentity"] = DescribeToken(tok);
        }
        catch (Exception ex)
        {
            // Surface the failure reason so misconfigurations (missing MI, wrong clientId,
            // IMDS blocked, etc.) are easy to diagnose from the HTTP response.
            logger.LogError(ex, "ManagedIdentityCredential failed");
            result["managedIdentity"] = new { error = ex.GetType().Name, message = ex.Message };
        }

        // 2) DefaultAzureCredential — walks the SDK credential chain (env vars, Visual Studio,
        //    Azure CLI, MI, etc.) and uses whichever succeeds first. Useful to confirm that
        //    production code relying on DAC ends up using the VM's Managed Identity rather
        //    than an unintended developer credential.
        try
        {
            var dac = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                // Hint DAC to use this UAMI when the MI step in the chain is reached.
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

        // 3) Optional: real Key Vault data-plane call. End-to-end check that the MI not only
        //    gets a token but is actually authorized on the target vault (requires the
        //    "Key Vault Secrets User" role, or equivalent access policy, on the MI).
        if (!string.IsNullOrEmpty(kvUri))
        {
            try
            {
                var client = new SecretClient(new Uri(kvUri), new ManagedIdentityCredential(clientId));
                // List up to 5 secret names — we only need proof that the call succeeded.
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
                // Typically a 403 here means the MI lacks the required RBAC role on the vault.
                result["keyVault"] = new { error = ex.GetType().Name, message = ex.Message };
            }
        }

        // 4) Optional: real Blob Storage data-plane call. Confirms the MI can authenticate
        //    to the storage account and has at least read permissions (requires the
        //    "Storage Blob Data Reader" role, or stronger, on the MI).
        if (!string.IsNullOrEmpty(blobUri))
        {
            try
            {
                var svc = new BlobServiceClient(new Uri(blobUri), new ManagedIdentityCredential(clientId));
                // List up to 5 container names — just a smoke test of the authorized call.
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
                // A 403 here usually indicates a missing data-plane RBAC role on the storage account.
                result["blob"] = new { error = ex.GetType().Name, message = ex.Message };
            }
        }

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

        // Return a pretty-printed JSON document containing the outcome of every check above.
        return new OkObjectResult(JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Extracts non-sensitive identity claims from a JWT access token so we can confirm
    /// which principal Azure actually authenticated as. The raw token itself is never
    /// returned — only safe metadata like tenant id, object id, audience and issuer.
    /// </summary>
    private static object DescribeToken(AccessToken tok)
    {
        // A JWT is "<header>.<payload>.<signature>". We only need the middle (payload) segment.
        var parts = tok.Token.Split('.');
        if (parts.Length < 2) return new { expiresOn = tok.ExpiresOn };

        // Convert from URL-safe Base64 (no padding) to standard Base64, then decode to JSON.
        string pad = parts[1].Replace('-', '+').Replace('_', '/');
        pad = pad.PadRight(pad.Length + (4 - pad.Length % 4) % 4, '=');
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(pad));
        using var doc = JsonDocument.Parse(json);

        // Small helper to read a string claim if it exists.
        string? Get(string n) => doc.RootElement.TryGetProperty(n, out var v) ? v.GetString() : null;

        // Project only the claims that are useful for diagnosing "who am I?".
        return new
        {
            expiresOn = tok.ExpiresOn,
            tid       = Get("tid"),       // tenant id
            oid       = Get("oid"),       // object id of the MI service principal in AAD
            appid     = Get("appid"),     // client (application) id of the MI
            xms_mirid = Get("xms_mirid"), // full ARM resource id of the identity (VM or UAMI)
            aud       = Get("aud"),       // intended audience / resource the token is valid for
            iss       = Get("iss"),       // token issuer (AAD authority)
        };
    }
}