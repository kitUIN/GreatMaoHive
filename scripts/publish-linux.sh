#!/usr/bin/env bash
# Invoked inside linux-build.Dockerfile; output is also tested on newer Linux.
set -euo pipefail
version=${1:?A release version is required}
[[ $version =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || exit 1
for application in GreatMaoNetUpdater GreatMaoNetHive GreatMaoNetBee; do
    dotnet publish "src/$application/$application.csproj" -c Release -r linux-x64 \
        "-p:Version=$version" -o "artifacts/publish/$application/linux-x64"
done
for application in GreatMaoNetHive GreatMaoNetBee; do
    destination="artifacts/publish/$application/linux-x64"
    cp artifacts/publish/GreatMaoNetUpdater/linux-x64/GreatMaoNetUpdater "$destination/"
    cp /tmp/libe_sqlite3.so "$destination/"
    test "$("$destination/$application" --version)" = "$version"
done
