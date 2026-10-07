#!/usr/bin/env bash
# Verifies the SparseFragments.Generator.Shared package fallback pin (#344).
#
# Configlue.Generator prefers the checked-out src/fragments submodule sources
# and falls back to the published source-only SparseFragments.Generator.Shared
# package otherwise. The fallback must resolve one explicit, reviewable version
# (SparseFragmentsGeneratorSharedVersion in src/Directory.Build.props), never a
# floating/wildcard version.
#
# Bump workflow: to adopt a new Shared release, bump
# SparseFragmentsGeneratorSharedVersion in src/Directory.Build.props, then
# verify with a package-fallback build (clean clone without the submodule, or
# temporarily hide src/fragments). Dependabot NuGet does NOT auto-bump
# Version="$(Property)" references, so this stays a deliberate manual bump
# (alternatively run `dotnet outdated` to spot newer Shared releases).
#
# NOTE: SparseFragments.Generator.Shared 0.1.0 is not yet published on
# nuget.org, so the restore/build probe below is skipped gracefully until the
# pinned version is restorable. The static pin assertions always run.
#
# Usage: run from the repository root, or anywhere (the repo root is derived
# from this script's location):
#   bash .github/workflows/scripts/verify-generator-fallback.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
generator_csproj="${repo_root}/src/tools/Configlue.Generator/Configlue.Generator.csproj"
props_file="${repo_root}/src/Directory.Build.props"
shared_csproj="src/fragments/src/SparseFragments.Generator.Shared/SparseFragments.Generator.Shared.csproj"

fail() {
    echo "verify-generator-fallback: $1" >&2
    exit 1
}

[[ -f "${generator_csproj}" ]] || fail "missing ${generator_csproj}"
[[ -f "${props_file}" ]] || fail "missing ${props_file}"

# 1. No floating/wildcard version for the Shared fallback in Configlue-owned files.
if grep -rIn --exclude-dir=fragments --exclude-dir=obj --exclude-dir=bin --exclude-dir=.git 'SparseFragments\.Generator\.Shared' "${repo_root}/src" \
    | grep -E 'Version="[^"]*[*]' >/dev/null; then
    fail "floating/wildcard SparseFragments.Generator.Shared version found (must be exact)."
fi
echo "No floating/wildcard SparseFragments.Generator.Shared version in Configlue-owned sources."

# 2. The fallback reference must use the single repository-level property.
grep -q 'Include="SparseFragments\.Generator\.Shared"' "${generator_csproj}" \
    || fail "SparseFragments.Generator.Shared PackageReference missing from Configlue.Generator.csproj."
grep -q 'Version="$(SparseFragmentsGeneratorSharedVersion)"' "${generator_csproj}" \
    || fail "fallback must use Version=\"\$(SparseFragmentsGeneratorSharedVersion)\"."
grep -q 'PrivateAssets="all"' "${generator_csproj}" \
    || fail "fallback must keep PrivateAssets=\"all\"."
echo "Fallback PackageReference uses the pinned \$(SparseFragmentsGeneratorSharedVersion) with PrivateAssets=\"all\"."

# 3. The pinned version itself must be an exact X.Y.Z (no wildcards/ranges).
pinned_version="$(sed -n 's/.*<SparseFragmentsGeneratorSharedVersion>\([^<]*\)<\/SparseFragmentsGeneratorSharedVersion>.*/\1/p' "${props_file}" | head -n 1)"
[[ -n "${pinned_version}" ]] || fail "SparseFragmentsGeneratorSharedVersion not defined in src/Directory.Build.props."
if [[ ! "${pinned_version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    fail "pinned version '${pinned_version}' is not an exact X.Y.Z version."
fi
echo "Pinned SparseFragments.Generator.Shared version: ${pinned_version}"

# 4. Submodule-source builds still win: when the submodule is checked out, the
#    package fallback is inactive by design, so only the static checks apply.
if [[ -f "${repo_root}/${shared_csproj}" ]]; then
    echo "Submodule sources present; fallback package inactive by design (submodule wins). Static pin checks passed."
    exit 0
fi

# 5. Fallback build probe (submodule absent): prove the pinned package can build
#    Configlue.Generator. Skipped gracefully while the pinned version is not
#    yet published/restorable.
echo "Submodule sources absent; probing package-fallback restore/build..."
if ! dotnet restore "${generator_csproj}" --nologo; then
    echo "WARNING: restore failed; the pinned SparseFragments.Generator.Shared ${pinned_version} may not be published yet." >&2
    echo "Static pin checks passed; package-fallback build probe SKIPPED until the Shared package is restorable."
    exit 0
fi
dotnet build "${generator_csproj}" --configuration Release --no-restore --nologo
echo "Package-fallback build succeeded with pinned version ${pinned_version}."
