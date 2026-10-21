cd "$(dirname "$0")"
BIN=server/bin/Release/net10.0/UploadSpike.dll
SPIKE_STORE=$PWD/store ASPNETCORE_URLS=http://127.0.0.1:5201 nohup dotnet $BIN --contentRoot $PWD/server > engine.log 2>&1 &
env SPIKE_STORE=$PWD/relaystore SPIKE_UPSTREAM=http://127.0.0.1:5201/ ${RELAY_TIMEOUT:+SPIKE_RELAY_TIMEOUT=$RELAY_TIMEOUT} ASPNETCORE_URLS=http://127.0.0.1:5200 nohup dotnet $BIN --contentRoot $PWD/server > relay.log 2>&1 &
sleep 4
