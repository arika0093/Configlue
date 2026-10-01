[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory
)

# Portable packages must ship every asset in the three-target policy.
$portablePackageAssets = @{
    'Configlue'                      = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Abstraction'          = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Core'                 = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'SparseFragments'                    = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Extensions.ComponentModel' = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Extensions.DI'        = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Extensions.MSOptions' = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Extensions.R3'        = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Testing'              = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Provider.Json'        = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Provider.Xml'         = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Provider.Yaml'        = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Source.Environment'   = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Source.CommandLine'   = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Resource.Http'        = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Resource.Redis'       = @('netstandard2.0', 'netstandard2.1', 'net10.0')
    'Configlue.Resource.S3'          = @('netstandard2.0', 'netstandard2.1', 'net10.0')
}

# Package-specific higher floors.
$flooredPackageAssets = @{
    'Configlue.Transformer.AES'             = @('netstandard2.1', 'net10.0')
    'Configlue.Hosting.AspNetCore'       = @('net10.0')
    'Configlue.Hosting.Blazor'           = @('net10.0')
    'Configlue.Source.PostgreSql'           = @('net8.0', 'net10.0')
    'Configlue.Source.PostgreSql.Migrations' = @('net8.0', 'net10.0')
}

$expectedPackageIds = @($portablePackageAssets.Keys) +
    @($flooredPackageAssets.Keys) +
    @('Configlue.Generator', 'Configlue.JsonSchema.MSBuild')

# Packages that backfill APIs missing from .NET Standard 2.0 and must not leak into 2.1.
$netStandard20OnlyDependencies = @(
    'Microsoft.Bcl.AsyncInterfaces',
    'System.Memory',
    'System.Threading.Tasks.Extensions'
)

$resolvedDirectory = Resolve-Path -LiteralPath $PackageDirectory
$packageFiles = @(Get-ChildItem -LiteralPath $resolvedDirectory -Filter '*.nupkg' -File)
if ($packageFiles.Count -ne $expectedPackageIds.Count) {
    throw "Expected $($expectedPackageIds.Count) NuGet packages, found $($packageFiles.Count) in '$resolvedDirectory'."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$foundPackageIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

foreach ($packageFile in $packageFiles) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packageFile.FullName)
    try {
        $nuspecEntries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) })
        if ($nuspecEntries.Count -ne 1) {
            throw "Package '$($packageFile.Name)' must contain exactly one .nuspec file."
        }

        $nuspecStream = $nuspecEntries[0].Open()
        try {
            $nuspecTextReader = [System.IO.StreamReader]::new($nuspecStream)
            try {
                [xml] $nuspec = $nuspecTextReader.ReadToEnd()
            }
            finally {
                $nuspecTextReader.Dispose()
            }
        }
        finally {
            $nuspecStream.Dispose()
        }

        $idNode = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='id']")
        if ($null -eq $idNode -or [string]::IsNullOrWhiteSpace($idNode.InnerText)) {
            throw "Package '$($packageFile.Name)' has no package ID in its .nuspec metadata."
        }

        $packageId = $idNode.InnerText
        if (-not $foundPackageIds.Add($packageId)) {
            throw "Package ID '$packageId' appears more than once in '$resolvedDirectory'."
        }

        if ($packageId -eq 'Configlue.Generator') {
            $analyzer = $archive.GetEntry('analyzers/dotnet/cs/Configlue.Generator.dll')
            if ($null -eq $analyzer -or $analyzer.Length -eq 0) {
                throw "Package '$packageId' is missing analyzers/dotnet/cs/Configlue.Generator.dll."
            }
            continue
        }

        if ($packageId -eq 'SparseFragments') {
            $analyzer = $archive.GetEntry('analyzers/dotnet/cs/SparseFragments.Generator.dll')
            if ($null -eq $analyzer -or $analyzer.Length -eq 0) {
                throw "Package '$packageId' is missing analyzers/dotnet/cs/SparseFragments.Generator.dll."
            }
        }

        if ($packageId -eq 'Configlue.JsonSchema.MSBuild') {
            $buildAssets = @(
                'build/Configlue.JsonSchema.MSBuild.props',
                'build/Configlue.JsonSchema.MSBuild.targets',
                'tasks/net10.0/Configlue.JsonSchema.MSBuild.dll'
            )
            foreach ($buildAsset in $buildAssets) {
                $entry = $archive.GetEntry($buildAsset)
                if ($null -eq $entry -or $entry.Length -eq 0) {
                    throw "Package '$packageId' is missing its build asset '$buildAsset'."
                }
            }

            continue
        }

        $expectedAssets =
            if ($portablePackageAssets.ContainsKey($packageId)) { $portablePackageAssets[$packageId] }
            else { $flooredPackageAssets[$packageId] }

        foreach ($asset in $expectedAssets) {
            $assemblyPath = "lib/$asset/$packageId.dll"
            $assembly = $archive.GetEntry($assemblyPath)
            if ($null -eq $assembly -or $assembly.Length -eq 0) {
                throw "Package '$packageId' is missing its '$asset' asset ('$assemblyPath')."
            }
        }

        if ($packageId -in @('Configlue')) {
            $analyzer = $archive.GetEntry('analyzers/dotnet/cs/Configlue.Generator.dll')
            if ($null -eq $analyzer -or $analyzer.Length -eq 0) {
                throw "Package '$packageId' is missing analyzers/dotnet/cs/Configlue.Generator.dll."
            }
        }

        if ($packageId -in $portablePackageAssets.Keys) {
            $netStandard21Group = $nuspec.SelectSingleNode(
                "/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='dependencies']/*[local-name()='group'][@targetFramework='.NETStandard2.1']"
            )
            if ($null -ne $netStandard21Group) {
                foreach ($dependencyNode in $netStandard21Group.SelectNodes("*[local-name()='dependency']")) {
                    $dependencyId = $dependencyNode.GetAttribute('id')
                    if ($dependencyId -in $netStandard20OnlyDependencies) {
                        throw "Package '$packageId' must not depend on netstandard2.0-only compatibility package '$dependencyId' for its netstandard2.1 asset."
                    }
                }
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

$missingPackageIds = @($expectedPackageIds | Where-Object { -not $foundPackageIds.Contains($_) })
if ($missingPackageIds.Count -gt 0) {
    throw "Missing NuGet package IDs: $($missingPackageIds -join ', ')."
}

$unexpectedPackageIds = @($foundPackageIds | Where-Object { $_ -notin $expectedPackageIds })
if ($unexpectedPackageIds.Count -gt 0) {
    throw "Unexpected NuGet package IDs: $($unexpectedPackageIds -join ', ')."
}

Write-Host "Verified $($foundPackageIds.Count) NuGet packages and their expected target assets."
