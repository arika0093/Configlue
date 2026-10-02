#!/usr/bin/env bash
# Verifies that the packed NuGet packages contain the expected target assets.
#
# Usage: verify-packages.sh <package-directory>
set -euo pipefail

package_directory="${1:-}"
if [[ -z "${package_directory}" ]]; then
    echo "Usage: $(basename "$0") <package-directory>" >&2
    exit 2
fi

if [[ ! -d "${package_directory}" ]]; then
    echo "Package directory '${package_directory}' does not exist." >&2
    exit 1
fi

# Portable packages must ship every asset in the three-target policy.
declare -A portable_package_assets=(
    [Configlue]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Abstraction]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Core]="netstandard2.0 netstandard2.1 net10.0"
    [SparseFragments]="netstandard2.0"
    [Configlue.Extensions.ComponentModel]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Extensions.DI]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Extensions.MSOptions]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Extensions.R3]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Testing]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Hosting.Maui]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Provider.Json]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Provider.MessagePack]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Provider.Xml]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Provider.Yaml]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Source.Environment]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Source.CommandLine]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.Http]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.Redis]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.S3]="netstandard2.0 netstandard2.1 net10.0"
)

# Package-specific higher floors.
declare -A floored_package_assets=(
    [Configlue.Transformer.AES]="netstandard2.1 net10.0"
    [Configlue.Transformer.Compression]="netstandard2.1 net10.0"
    [Configlue.Hosting.AspNetCore]="net10.0"
    [Configlue.Hosting.Blazor]="net10.0"
    [Configlue.Hosting.Avalonia]="net8.0 net10.0"
    [Configlue.Hosting.Godot]="net8.0 net10.0"
    [Configlue.Source.PostgreSql]="net8.0 net10.0"
    [Configlue.Source.PostgreSql.Migrations]="net8.0 net10.0"
)

expected_package_ids=(
    "${!portable_package_assets[@]}"
    "${!floored_package_assets[@]}"
    Configlue.Generator
    Configlue.JsonSchema.MSBuild
)

# Packages that backfill APIs missing from .NET Standard 2.0 and must not leak into 2.1.
netstandard20_only_dependencies=(
    Microsoft.Bcl.AsyncInterfaces
    System.Memory
    System.Threading.Tasks.Extensions
)

package_files=()
while IFS= read -r package_file; do
    package_files+=("${package_file}")
done < <(find "${package_directory}" -maxdepth 1 -type f -name '*.nupkg' -print | sort)

if [[ ${#package_files[@]} -ne ${#expected_package_ids[@]} ]]; then
    echo "Expected ${#expected_package_ids[@]} NuGet packages, found ${#package_files[@]} in '${package_directory}'." >&2
    exit 1
fi

require_entry() {
    local entry="$1"
    local size
    size="$(unzip -p "${package_file}" "${entry}" 2>/dev/null | wc -c | tr -d '[:space:]')"
    if [[ -z "${size}" || "${size}" -eq 0 ]]; then
        echo "Package '${package_id}' is missing a non-empty entry '${entry}'." >&2
        exit 1
    fi
}

declare -A found_package_ids=()

for package_file in "${package_files[@]}"; do
    entries="$(unzip -Z1 "${package_file}")"

    nuspec_count="$(grep -c -i '\.nuspec$' <<<"${entries}" || true)"
    if [[ "${nuspec_count}" -ne 1 ]]; then
        echo "Package '$(basename "${package_file}")' must contain exactly one .nuspec file." >&2
        exit 1
    fi

    nuspec="$(unzip -p "${package_file}" '*.nuspec')"
    package_id="$(sed -n 's/.*<id>\([^<]*\)<\/id>.*/\1/p' <<<"${nuspec}" | head -n 1)"
    if [[ -z "${package_id}" ]]; then
        echo "Package '$(basename "${package_file}")' has no package ID in its .nuspec metadata." >&2
        exit 1
    fi

    if [[ -n "${found_package_ids[${package_id}]:-}" ]]; then
        echo "Package ID '${package_id}' appears more than once in '${package_directory}'." >&2
        exit 1
    fi
    found_package_ids[${package_id}]=1

    case "${package_id}" in
        Configlue.Generator)
            require_entry 'analyzers/dotnet/cs/Configlue.Generator.dll'
            continue
            ;;
        SparseFragments)
            require_entry 'analyzers/dotnet/cs/SparseFragments.Generator.dll'
            if grep -Eq '<dependency[^>]*id="Configlue(\.|")' <<<"${nuspec}"; then
                echo "SparseFragments must not depend on Configlue packages." >&2
                exit 1
            fi
            if grep -q 'analyzers/dotnet/cs/Configlue.Generator.dll' <<<"${entries}"; then
                echo "SparseFragments must carry only its own model generator." >&2
                exit 1
            fi
            ;;
        Configlue.JsonSchema.MSBuild)
            require_entry 'build/Configlue.JsonSchema.MSBuild.props'
            require_entry 'build/Configlue.JsonSchema.MSBuild.targets'
            require_entry 'tasks/net10.0/Configlue.JsonSchema.MSBuild.dll'
            continue
            ;;
    esac

    if [[ -n "${portable_package_assets[${package_id}]:-}" ]]; then
        expected_assets="${portable_package_assets[${package_id}]}"
    else
        expected_assets="${floored_package_assets[${package_id}]}"
    fi

    read -r -a asset_list <<<"${expected_assets}"
    for asset in "${asset_list[@]}"; do
        require_entry "lib/${asset}/${package_id}.dll"
    done

    if [[ "${package_id}" == "Configlue" ]]; then
        require_entry 'analyzers/dotnet/cs/Configlue.Generator.dll'
    fi

    if [[ -n "${portable_package_assets[${package_id}]:-}" ]]; then
        in_group=0
        while IFS= read -r line; do
            if [[ "${line}" == *'<group '* ]]; then
                if [[ "${line}" == *'targetFramework=".NETStandard2.1"'* ]]; then
                    in_group=1
                else
                    in_group=0
                fi
                continue
            fi
            if [[ "${line}" == *'</group>'* ]]; then
                in_group=0
                continue
            fi
            if [[ "${in_group}" -eq 1 && "${line}" == *'<dependency '* ]]; then
                dependency_id="$(sed -n 's/.*id="\([^"]*\)".*/\1/p' <<<"${line}")"
                for dependency in "${netstandard20_only_dependencies[@]}"; do
                    if [[ "${dependency_id}" == "${dependency}" ]]; then
                        echo "Package '${package_id}' must not depend on netstandard2.0-only compatibility package '${dependency_id}' for its netstandard2.1 asset." >&2
                        exit 1
                    fi
                done
            fi
        done <<<"${nuspec}"
    fi
done

missing_package_ids=()
for expected_id in "${expected_package_ids[@]}"; do
    if [[ -z "${found_package_ids[${expected_id}]:-}" ]]; then
        missing_package_ids+=("${expected_id}")
    fi
done
if [[ ${#missing_package_ids[@]} -gt 0 ]]; then
    echo "Missing NuGet package IDs: ${missing_package_ids[*]}." >&2
    exit 1
fi

unexpected_package_ids=()
for found_id in "${!found_package_ids[@]}"; do
    if [[ -z "${portable_package_assets[${found_id}]:-}" && \
        -z "${floored_package_assets[${found_id}]:-}" && \
        "${found_id}" != "Configlue.Generator" && \
        "${found_id}" != "Configlue.JsonSchema.MSBuild" ]]; then
        unexpected_package_ids+=("${found_id}")
    fi
done
if [[ ${#unexpected_package_ids[@]} -gt 0 ]]; then
    echo "Unexpected NuGet package IDs: ${unexpected_package_ids[*]}." >&2
    exit 1
fi

echo "Verified ${#found_package_ids[@]} NuGet packages and their expected target assets."
