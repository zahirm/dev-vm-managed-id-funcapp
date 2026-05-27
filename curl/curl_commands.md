curl "http://localhost:7071/api/whoami?clientId=09779c4f-b075-4dda-9e56-4e350e485459"

curl "http://localhost:7071/api/whoami?kvUri=https://kv-retz-test-01.vault.azure.net/"

curl.exe "http://localhost:7071/api/whoami?kvUri=https://kv-retz-test-01.vault.azure.net/&clientId=09779c4f-b075-4dda-9e56-4e350e485459"

curl.exe "http://localhost:7071/api/whoami?blobUri=https://storragtestz01.blob.core.windows.net/&clientId=09779c4f-b075-4dda-9e56-4e350e485459"

curl.exe "http://localhost:7071/api/whoami?clientId=09779c4f-b075-4dda-9e56-4e350e485459&sbNamespace=servbuszatst01.servicebus.windows.net&sbQueue=testqu01"