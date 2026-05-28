curl "http://localhost:7071/api/whoami?clientId=yourclientid"

curl "http://localhost:7071/api/whoami?kvUri=https://kv-retz-test-01.vault.azure.net/"

curl.exe "http://localhost:7071/api/whoami?kvUri=https://kv-retz-test-01.vault.azure.net/&clientId=yourclientid"

curl.exe "http://localhost:7071/api/whoami?blobUri=https://storragtestz01.blob.core.windows.net/&clientId=yourclientid"

curl.exe "http://localhost:7071/api/whoami?clientId=yourclientid&sbNamespace=servbuszatst01.servicebus.windows.net&sbQueue=testqu01"