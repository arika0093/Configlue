#!/usr/bin/env bash
# Verifies packed NuGet package integrity: expected package IDs, target
# assets, and analyzer/build assets that packing can silently omit.
#
# Consumer coverage in test-package-consumers.yaml is intentionally narrow:
# package-basic exercises only the Configlue facade (net8.0/net10.0
# restore/build/run), package-sparse exercises only SparseFragments (plus a
# netstandard2.0/net48 build), and the native-aot jobs exercise only Configlue
# plus the MessagePack provider. These positive runs prove that required facade
# dependencies resolve, but they cannot detect extra opt-in dependencies, a
# netstandard2.0-only compat package leaking into the netstandard2.1 group,
# SparseFragments independence beyond its own restore, or DevTools/Blazor
# graphs that no consumer references.
#
# Per #274 (positive-consumer-first, minimal negatives), detailed composition
# drift outside the checks below is intentionally ungated and accepted as
# residual risk:
# - #261 (Configlue facade standard boundary): a missing required facade
#   dependency fails the package-basic consumer build, but an extra opt-in
#   dependency (DI/HTTP/CommandLine/YAML/XML/MessagePack/transformers) does
#   not fail and is not gated here. A lightweight transitive-closure smoke
#   assertion lives in test-package-consumers.yaml, not in this script.
# - #264 (Hosting/DevTools boundary): only leaks with material deployment
#   consequence stay gated here. Hosting.* must not depend on
#   Configlue.DevTools* (dev tooling in production graphs), and only
#   Configlue.DevTools.Web may depend on BlazorMonaco. The BlazorMonaco check
#   stays because no consumer references DevTools.Web, so this nuspec check is
#   the only cheap containment gate.
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
    [SparseFragments.JsonPatch]="netstandard2.0"
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
    [Configlue.Source.Http]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.Redis]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.S3]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.Vault]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Source.Consul]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.Etcd]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.AzureAppConfiguration]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.AzureKeyVault]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Source.Ssm]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.SecretsManager]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.AwsAppConfig]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.GoogleSecretManager]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.Kubernetes]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.AzureBlob]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.Resource.Gcs]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.DevTools]="netstandard2.0 netstandard2.1 net10.0"
    [Configlue.DevTools.Maui]="netstandard2.0 netstandard2.1 net10.0"
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
    [Configlue.DevTools.Web]="net10.0"
    [Configlue.DevTools.Godot]="net8.0 net10.0"
)

expected_package_ids=(
    "${!portable_package_assets[@]}"
    "${!floored_package_assets[@]}"
    Configlue.Generator
    Configlue.JsonSchema.MSBuild
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

    # #264 package boundaries (negative graph assertions with material
    # deployment consequence): ordinary Hosting packages must not pull
    # DevTools into production graphs, and Blazor/Monaco assets must stay
    # contained in Configlue.DevTools.Web (no consumer references DevTools.Web,
    # so this nuspec check is the only cheap gate).
    case "${package_id}" in
        Configlue.Hosting.*)
            if grep -Eq '<dependency[^>]*id="Configlue\.DevTools' <<<"${nuspec}"; then
                echo "Package '${package_id}' must not depend on Configlue.DevTools* (#264)." >&2
                exit 1
            fi
            ;;
    esac
    if [[ "${package_id}" != "Configlue.DevTools.Web" ]]; then
        if grep -Eq '<dependency[^>]*id="BlazorMonaco"' <<<"${nuspec}"; then
            echo "Package '${package_id}' must not depend on BlazorMonaco (#264)." >&2
            exit 1
        fi
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
