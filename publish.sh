#!/bin/sh
# Bygger Release for Windows/IIS til deploy/net8.0 og deploy/net10.0.
set -e

cd "$(dirname "$0")"

for framework in net8.0 net10.0; do
	rm -rf "deploy/$framework"
	dotnet publish LoggViewer.csproj -c Release -f "$framework" -r win-x64 --self-contained false -o "deploy/$framework"
done

echo
echo "Klar til deploy:"
for framework in net8.0 net10.0; do
	echo "  deploy/$framework ($(ls "deploy/$framework" | wc -l | tr -d ' ') filer)"
done
