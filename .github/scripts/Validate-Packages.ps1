param([string] $PackageDirectory = 'artifacts/packages')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$public = @(
    'README.md', 'README.en.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md',
    'docs/api.md', 'docs/architecture.md', 'docs/nuget.md', 'native/projection/README.md',
    'licenses/minecraft-renderer/LICENSE', 'licenses/minecraft-renderer/SOURCE.md',
    'licenses/schematic-editor/LICENSE', 'licenses/schematic-editor/SOURCE.md',
    'lib/net10.0/RZeroF1.Minecraft.Projection.dll', 'lib/net10.0/RZeroF1.Minecraft.Projection.xml',
    'runtimes/win-x64/native/rzero_projection_d3d11.dll'
)
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -File | Where-Object Extension -in '.nupkg', '.snupkg')
if (-not ($packages | Where-Object Extension -eq '.nupkg') -or -not ($packages | Where-Object Extension -eq '.snupkg')) {
    throw 'Both nupkg and snupkg must be validated.'
}
foreach ($package in $packages) {
    $symbols = $package.Extension -eq '.snupkg'
    $allowed = if ($symbols) { @('lib/net10.0/RZeroF1.Minecraft.Projection.pdb') } else { $public }
    $zip = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            $path = $entry.FullName.Replace('\', '/')
            if (-not $seen.Add($path)) { throw "Duplicate package entry: $path" }
            $generated = $path -ceq '[Content_Types].xml' -or $path -ceq '_rels/.rels' -or
                $path -ceq 'RZeroF1.Minecraft.Projection.nuspec' -or
                $path -cmatch '^package/services/metadata/core-properties/(?:[a-f0-9]+|nuget)\.psmdcp$'
            if (-not $generated -and $path -cnotin $allowed) {
                throw "Unexpected payload in $($package.Name): $path"
            }
            # A closed payload list also rejects future, differently named internal folders.
            if ($path -match '(?i)(internal|research|artifact|donot.?submit|do.not.submit|\.zip$|\.litematic$|\.schem$)') {
                throw "Private payload in $($package.Name): $path"
            }
            if ($path -in $public -and $path -notmatch '\.(dll|xml)$') {
                $reader = [IO.StreamReader]::new($entry.Open())
                try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
                if ($text -match '(?i)DO\s*NOT\s*SUBMIT|DONOTSUBMIT') {
                    throw "Private-content marker in public payload: $path"
                }
            }
        }
        foreach ($path in $allowed) {
            if (-not $seen.Contains($path)) { throw "Missing payload in $($package.Name): $path" }
        }
        Write-Host "PASS $($package.Name): $($seen.Count) entries; closed public payload only."
    }
    finally { $zip.Dispose() }
}
