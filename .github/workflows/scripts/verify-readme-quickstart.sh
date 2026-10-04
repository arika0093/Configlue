#!/usr/bin/env bash
# Verifies that the README Quick Start sample still compiles and runs.
#
# Extracts the first csharp block under "## Quick Start" in README.md,
# strips file-based-app directives (shebang and #:package), and builds it
# as a net10.0 console app against the packed Configlue packages. The sample
# is then executed in an isolated working directory (WithLocal writes
# settings.json relative to the current directory).
#
# Usage: verify-readme-quickstart.sh <package-directory> [readme-path]
set -euo pipefail

package_directory="${1:-}"
readme_path="${2:-README.md}"

if [[ -z "${package_directory}" ]]; then
    echo "Usage: $(basename "$0") <package-directory> [readme-path]" >&2
    exit 2
fi
if [[ ! -f "${readme_path}" ]]; then
    echo "README file '${readme_path}' does not exist." >&2
    exit 1
fi

package="$(ls "${package_directory}"/SparseFragments.*.nupkg 2>/dev/null | head -n 1 || true)"
if [[ -z "${package}" ]]; then
    echo "No SparseFragments package found in '${package_directory}'." >&2
    exit 1
fi
version="$(basename "${package}" | sed -E 's/^SparseFragments\.(.+)\.nupkg$/\1/')"
if [[ -z "${version}" ]]; then
    echo "Could not resolve package version from '$(basename "${package}")'." >&2
    exit 1
fi
feed="$(realpath "${package_directory}")"

workdir="$(mktemp -d)"
trap 'rm -rf "${workdir}"' EXIT

# Extract the first ```csharp block after "## Quick Start".
awk '
    /^## Quick Start/ { in_section = 1; next }
    in_section && /^## / { exit }
    in_section && /^```csharp/ { in_block = 1; next }
    in_block && /^```/ { exit }
    in_block { print }
' "${readme_path}" | grep -v -e '^#!/usr/bin/env dotnet' -e '^#:' >"${workdir}/Program.cs"

if [[ ! -s "${workdir}/Program.cs" ]]; then
    echo "No Quick Start csharp block found in '${readme_path}'." >&2
    exit 1
fi

# Guard against CS8803 regressions: type declarations must come after all
# top-level statements, so the model class has to be at the end of the sample.
shutdown_line="$(grep -n 'ShutdownAsync' "${workdir}/Program.cs" | head -n 1 | cut -d: -f1 || true)"
class_line="$(grep -n 'class AppSettings' "${workdir}/Program.cs" | head -n 1 | cut -d: -f1 || true)"
if [[ -z "${shutdown_line}" || -z "${class_line}" ]]; then
    echo "Quick Start sample must contain ShutdownAsync and the AppSettings model." >&2
    exit 1
fi
if [[ "${class_line}" -lt "${shutdown_line}" ]]; then
    echo "Quick Start model declaration (line ${class_line}) must come after top-level statements (ShutdownAsync on line ${shutdown_line}); otherwise file-based apps fail with CS8803." >&2
    exit 1
fi

cat >"${workdir}/Readme.QuickStart.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Configlue" Version="${version}" />
  </ItemGroup>
</Project>
EOF

dotnet build "${workdir}/Readme.QuickStart.csproj" \
    --configuration Release \
    -p:RestoreAdditionalProjectSources="${feed}"

rundir="$(mktemp -d)"
trap 'rm -rf "${workdir}" "${rundir}"' EXIT
output="$((cd "${rundir}" && dotnet "${workdir}/bin/Release/net10.0/Readme.QuickStart.dll") 2>&1)"
echo "${output}"
if [[ "${output}" != *"Hello, "* ]]; then
    echo "README Quick Start sample did not reach its greeting output." >&2
    exit 1
fi

echo "README Quick Start sample verified (Configlue ${version})."
