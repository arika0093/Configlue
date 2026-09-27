[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory
)

$expectedPackageIds = @(
    'Configlue',
    'Configlue.Abstraction',
    'Configlue.Core',
    'Configlue.Extensions.DI',
    'Configlue.Extensions.MSOptions',
    'Configlue.Generator',
    'Configlue.Testing',
    'Configlue.Provider.Json',
    'Configlue.Provider.Xml',
    'Configlue.Provider.Yaml',
    'Configlue.Source.Environment',
    'Configlue.Source.CommandLine',
    'Configlue.Source.Common',
    'Configlue.Resource.Zip',
    'Configlue.Resource.Http',
    'Configlue.Resource.Http.AspNetCore'
)

$resolvedDirectory = Resolve-Path -LiteralPath $PackageDirectory
$packageFiles = @(Get-ChildItem -LiteralPath $resolvedDirectory -Filter '*.nupkg' -File)
if ($packageFiles.Count -ne $expectedPackageIds.Count) {
    throw "Expected $($expectedPackageIds.Count) NuGet packages, found $($packageFiles.Count) in '$resolvedDirectory'."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$foundPackageIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$configlueDependencies = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
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

        if ($packageId -eq 'Configlue') {
            $dependencyNodes = $nuspec.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='dependencies']//*[local-name()='dependency']")
            foreach ($dependencyNode in $dependencyNodes) {
                [void] $configlueDependencies.Add($dependencyNode.GetAttribute('id'))
            }
        }

        if ($packageId -ne 'Configlue') {
            $assemblyPath = if ($packageId -eq 'Configlue.Generator') {
                'analyzers/dotnet/cs/Configlue.Generator.dll'
            }
            else {
                "lib/net10.0/$packageId.dll"
            }
            $assembly = $archive.GetEntry($assemblyPath)
            if ($null -eq $assembly -or $assembly.Length -eq 0) {
                throw "Package '$packageId' is missing '$assemblyPath'."
            }
        }
        else {
            $libAssemblies = @($archive.Entries | Where-Object {
                $_.FullName -match '^lib/.+\.dll$'
            })
            if ($libAssemblies.Count -gt 0) {
                throw "Meta-package 'Configlue' must not contain implementation assemblies."
            }
        }

        if ($packageId -in @('Configlue', 'Configlue.Generator')) {
            $analyzer = $archive.GetEntry('analyzers/dotnet/cs/Configlue.Generator.dll')
            if ($null -eq $analyzer -or $analyzer.Length -eq 0) {
                throw "Package '$packageId' is missing analyzers/dotnet/cs/Configlue.Generator.dll."
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

$requiredMetaDependencies = @(
    'Configlue.Abstraction',
    'Configlue.Core',
    'Configlue.Extensions.DI',
    'Configlue.Provider.Json',
    'Configlue.Resource.Http',
    'Configlue.Source.Common',
    'Configlue.Source.Environment'
)
$missingMetaDependencies = @(
    $requiredMetaDependencies | Where-Object { -not $configlueDependencies.Contains($_) }
)
if ($missingMetaDependencies.Count -gt 0) {
    throw "The Configlue meta-package is missing dependencies: $($missingMetaDependencies -join ', ')."
}

Write-Host "Verified $($foundPackageIds.Count) NuGet packages and generator analyzer contents."
