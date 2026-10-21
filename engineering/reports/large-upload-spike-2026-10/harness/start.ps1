# Starts the spike harness on Windows so another device on the home network can open it.
# Engine-like store on port 5201, Dashboard-like relay on port 5200. Not product code.
param([string]$Store = "$PSScriptRoot\store", [int]$RelayTimeoutSeconds = 100)
dotnet build "$PSScriptRoot\server\UploadSpike.csproj" -c Release
$bin = "$PSScriptRoot\server\bin\Release\net10.0\UploadSpike.dll"
$env:SPIKE_STORE = $Store; $env:ASPNETCORE_URLS = "http://0.0.0.0:5201"
Start-Process dotnet -ArgumentList $bin, "--contentRoot", "$PSScriptRoot\server" -NoNewWindow
$env:SPIKE_STORE = "$PSScriptRoot\relaystore"; $env:SPIKE_UPSTREAM = "http://127.0.0.1:5201/"
$env:SPIKE_RELAY_TIMEOUT = "$RelayTimeoutSeconds"; $env:ASPNETCORE_URLS = "http://0.0.0.0:5200"
Start-Process dotnet -ArgumentList $bin, "--contentRoot", "$PSScriptRoot\server" -NoNewWindow
Write-Host "Open http://<this-pc-name>:5200/ from the other device. Base path /relay goes through the relay."
