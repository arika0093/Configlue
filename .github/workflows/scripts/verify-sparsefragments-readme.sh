#!/usr/bin/env bash
# Verifies the standalone SparseFragments README samples (#315).
#
# The canonical compile-checked source is
# tests/fixtures/consumers/package-sparse-readme/Program.cs, which mirrors the
# Usage sections of src/fragments/SparseFragments/README.md (model shape,
# sparse construction, nested fragments, merge, typed patch, deep clone, and
# one SparseFragments.JsonPatch round-trip). This script guards against drift
# between the README and that canonical source, then builds and runs the
# fixture against the packed packages so CI fails when the public generated
# API breaks the documented samples.
#
# Usage: verify-sparsefragments-readme.sh <package-directory> [readme-path]
set -euo pipefail

package_directory="${1:-}"
readme_path="${2:-src/fragments/SparseFragments/README.md}"
fixture_dir="tests/fixtures/consumers/package-sparse-readme"
fixture_csproj="${fixture_dir}/PackageSparse.Readme.Consumer.csproj"
fixture_program="${fixture_dir}/Program.cs"

if [[ -z "${package_directory}" ]]; then
    echo "Usage: $(basename "$0") <package-directory> [readme-path]" >&2
    exit 2
fi
if [[ ! -f "${readme_path}" ]]; then
    echo "README file '${readme_path}' does not exist." >&2
    exit 1
fi
if [[ ! -f "${fixture_csproj}" || ! -f "${fixture_program}" ]]; then
    echo "Canonical README fixture missing under '${fixture_dir}'." >&2
    exit 1
fi

# 1. The merge sample constructs Child.Fragment directly, so Child must be
# generated. Fail when the README model drifts back to an undecorated Child.
if ! grep -B1 -E 'public partial class Child' "${readme_path}" | grep -q 'SparseFragmentModel'; then
    echo "README must decorate 'Child' with [SparseFragmentModel]: the merge sample constructs 'new Child.Fragment { ... }' directly." >&2
    exit 1
fi
if ! grep -B1 -E 'public partial class Child' "${fixture_program}" | grep -q 'SparseFragmentModel'; then
    echo "Canonical fixture must decorate 'Child' with [SparseFragmentModel]." >&2
    exit 1
fi

# 2. Representative API tokens must appear in both the README and the
# canonical fixture so the fixture cannot silently drift from the docs.
required_tokens=(
    '[SparseFragmentModel]'
    'new Settings.Fragment'
    'new Child.Fragment'
    '.Merge('
    'Settings.Fragment.Diff'
    'new Settings.Patch'
    '.Unset()'
    '.SetNull()'
    'DeepClone'
    'ToBuilder'
    'FromJsonPatch'
    'ToJsonPatch'
)
for token in "${required_tokens[@]}"; do
    if ! grep -F -q "${token}" "${readme_path}"; then
        echo "README '${readme_path}' is missing required sample token '${token}'." >&2
        exit 1
    fi
    if ! grep -F -q "${token}" "${fixture_program}"; then
        echo "Canonical fixture '${fixture_program}' is missing token '${token}' present in the README; keep them in sync." >&2
        exit 1
    fi
done

# 3. Compile and run the canonical fixture against the packed packages.
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
if [[ ! -f "${package_directory}/SparseFragments.JsonPatch.${version}.nupkg" ]]; then
    echo "No SparseFragments.JsonPatch package version '${version}' found in '${package_directory}'." >&2
    exit 1
fi
feed="$(realpath "${package_directory}")"

for framework in net8.0 net10.0; do
    dotnet build "${fixture_csproj}" \
        --configuration Release --framework "${framework}" \
        -p:ConfigluePackageVersion="${version}" \
        -p:RestoreAdditionalProjectSources="${feed}"
    output="$(dotnet "${fixture_dir}/bin/Release/${framework}/PackageSparse.Readme.Consumer.dll" 2>&1)"
    echo "${output}"
    if [[ "${output}" != *"SparseFragments README consumer passed."* ]]; then
        echo "SparseFragments README fixture did not reach its success marker on '${framework}'." >&2
        exit 1
    fi
done

echo "SparseFragments README samples verified (SparseFragments ${version})."
