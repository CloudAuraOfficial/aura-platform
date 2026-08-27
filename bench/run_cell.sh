#!/usr/bin/env bash
# E06 cell runner for the shared dispatcher: argv[1] = cell JSON; last stdout line = metrics JSON.
# Mock provider only — Aura.Bench references no cloud SDK (see csproj: zero package references).
set -euo pipefail; cd "$(dirname "$0")/Aura.Bench"
DLL=bin/Release/net8.0/Aura.Bench.dll
[ -f "$DLL" ] || dotnet build -c Release -nologo -v q >&2
exec dotnet "$DLL" "${1:-{\}}"
