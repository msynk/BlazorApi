#!/usr/bin/env bash
# Checks the generator against large published OpenAPI contracts: each is downloaded, validated, generated (with
# default options and with every optional output) and compiled against BlazorApi.Runtime and BlazorApi.Testing.
# Fails when a contract does not load, a generation fails, or the generated code has a compiler error or warning.
#
#   eng/real-world-contracts.sh [work-directory]     (default: artifacts/real-world)
#
# The contracts come from their publishers' default branches, so results can change as they evolve. Not part of CI:
# it downloads about 50 MB and compiles several million lines.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
work="${1:-$root/artifacts/real-world}"
mkdir -p "$work/specs" "$work/out" "$work/build"

contracts=(
  "github-3.0|api.github.com.yaml|https://raw.githubusercontent.com/github/rest-api-description/main/descriptions/api.github.com/api.github.com.yaml"
  "github-3.1|api.github.com.yaml|https://raw.githubusercontent.com/github/rest-api-description/main/descriptions-next/api.github.com/api.github.com.yaml"
  "stripe|spec3.json|https://raw.githubusercontent.com/stripe/openapi/master/openapi/spec3.json"
  "openai|openapi.yaml|https://raw.githubusercontent.com/openai/openai-openapi/master/openapi.yaml"
  "kubernetes|api__v1_openapi.json|https://raw.githubusercontent.com/kubernetes/kubernetes/master/api/openapi-spec/v3/api__v1_openapi.json"
  "twilio|twilio_api_v2010.json|https://raw.githubusercontent.com/twilio/twilio-oai/main/spec/json/twilio_api_v2010.json"
  "asana|asana_oas.yaml|https://raw.githubusercontent.com/Asana/openapi/master/defs/asana_oas.yaml"
  "jira|swagger-v3.v3.json|https://developer.atlassian.com/cloud/jira/platform/swagger-v3.v3.json"
  "box|openapi.json|https://raw.githubusercontent.com/box/box-openapi/main/openapi.json"
)

dotnet build "$root/src/BlazorApi.Cli" -c Release -nologo -v q > /dev/null
api_gen="dotnet $root/src/BlazorApi.Cli/bin/Release/net10.0/api-gen.dll"

# MSBuild needs native paths (Git Bash on Windows gives /c/...); the empty Directory.Build files stop the repository's
# own build settings from applying to the generated code.
native_root="$(cygpath -m "$root" 2>/dev/null || echo "$root")"
echo "<Project />" > "$work/build/Directory.Build.props"
echo "<Project />" > "$work/build/Directory.Build.targets"
cat > "$work/build/Compile.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <NoWarn>\$(NoWarn);CS1591</NoWarn>
    <OutputPath>bin/\$(GenName)/</OutputPath>
    <IntermediateOutputPath>obj/\$(GenName)/</IntermediateOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="\$(GenDir)/**/*.cs" />
    <ProjectReference Include="$native_root/src/BlazorApi.Runtime/BlazorApi.Runtime.csproj" />
    <ProjectReference Include="$native_root/src/BlazorApi.Testing/BlazorApi.Testing.csproj" />
  </ItemGroup>
</Project>
EOF

failed=0
check() {
  local name="$1" spec="$2"; shift 2
  local out="$work/out/$name"
  rm -rf "$out"
  if ! $api_gen generate "$spec" --output "$out" --namespace "Gen.${name//[-.]/_}" --quiet "$@" > "$work/$name.generate.log" 2>&1; then
    echo "  $name: generation failed (see $work/$name.generate.log)"; failed=1; return
  fi

  local log="$work/$name.build.log"
  if ! dotnet build "$work/build/Compile.csproj" -c Release -nologo -v q -p:GenDir="$(cygpath -m "$out" 2>/dev/null || echo "$out")" -p:GenName="$name" > "$log" 2>&1; then
    echo "  $name: does not compile ($(grep -c ' error ' "$log") errors, see $log)"; failed=1; return
  fi

  local warnings
  warnings=$( (grep ' warning CS' "$log" || true) | sort -u | wc -l)
  if [ "$warnings" -gt 0 ]; then
    echo "  $name: $warnings compiler warnings (see $log)"; failed=1; return
  fi

  echo "  $name: $(find "$out" -name '*.cs' | wc -l) files compile cleanly"
}

for entry in "${contracts[@]}"; do
  IFS='|' read -r name file url <<< "$entry"
  mkdir -p "$work/specs/$name"
  spec="$work/specs/$name/$file"
  [ -f "$spec" ] || curl -sSfL --max-time 300 -o "$spec" "$url"
  echo "$name"
  if ! $api_gen validate "$spec" > "$work/$name.validate.log" 2>&1; then
    echo "  does not load (see $work/$name.validate.log)"; failed=1; continue
  fi

  tail -n 1 "$work/$name.validate.log" | sed 's/^.*: /  /'
  check "$name" "$spec"
  check "$name-all" "$spec" --option generateJsonContext=true --option generateMocks=true --option unknownEnumValues=unrecognized
done

exit $failed
