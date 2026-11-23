#!/usr/bin/env bash
# Run only pure AP-01 fixture checks when VSTest cannot open a local socket.
# Requires a restored .NET test project; never starts a stack or a database.
set -euo pipefail
repo_root=$(cd "$(dirname "$0")/.." && pwd)
project="$repo_root/apps/api/tests/Ringfour.Api.Tests/Ringfour.Api.Tests.csproj"
DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet build "$project" --no-restore \
  -m:1 -p:UseSharedCompilation=false --verbosity minimal
fixture_check_dir=$(mktemp -d /tmp/ringfour-app-contracts.XXXXXXXX)
trap 'rm -rf -- "$fixture_check_dir"' EXIT
# Use XML serialization rather than interpolating a checkout path into markup.
python3 - "$repo_root" "$fixture_check_dir" <<'PY'
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
repo, output = map(Path, sys.argv[1:])
project = ET.Element('Project', Sdk='Microsoft.NET.Sdk')
properties = ET.SubElement(project, 'PropertyGroup')
for key, value in [('OutputType', 'Exe'), ('TargetFramework', 'net10.0'), ('ImplicitUsings', 'enable')]:
    ET.SubElement(properties, key).text = value
items = ET.SubElement(project, 'ItemGroup')
ET.SubElement(items, 'Reference', Include=str(repo / 'apps/api/tests/Ringfour.Api.Tests/bin/Debug/net10.0/*.dll'))
for folder in ['schemas', 'fixtures']:
    ET.SubElement(items, 'Content', Include=str(repo / 'packages/contracts' / folder / '**/*.json'),
                  Link='contracts/' + folder + '/%(RecursiveDir)%(Filename)%(Extension)',
                  CopyToOutputDirectory='PreserveNewest')
ET.ElementTree(project).write(output / 'FixtureCheck.csproj', encoding='unicode')
PY
cat > "$fixture_check_dir/Program.cs" <<'CS'
var tests = new Ringfour.Api.Tests.AppContractFixtureTests();
foreach (var (path, valid) in new[] {
    ("apps/v1/api.json", true), ("apps/v1/requests.json", true), ("apps/v1/errors.json", true),
    ("apps/v1/api-rejected.json", false), ("device/apps/v1/positive.json", true), ("device/apps/v1/rejected.json", false) })
{
    tests.FixturesMatchIndependentSchemaReader(path, valid);
    Console.WriteLine($"PASS {path}");
}
tests.ManifestSemanticFixturesMatchIndependentReader();
Console.WriteLine("PASS manifest semantic fixtures");
tests.ManifestCanonicalBytesMatchIndependentReader();
Console.WriteLine("PASS canonical manifest digest; 8 C# contract checks passed in process");
CS
mkdir "$fixture_check_dir/empty-feed"
DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet build "$fixture_check_dir/FixtureCheck.csproj" \
  --source "$fixture_check_dir/empty-feed" -m:1 -p:UseSharedCompilation=false --verbosity minimal
DOTNET_EnableDiagnostics=0 dotnet "$fixture_check_dir/bin/Debug/net10.0/FixtureCheck.dll"
