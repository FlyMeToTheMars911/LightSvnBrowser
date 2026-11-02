[CmdletBinding()]
param(
    [string]$SvnBinDirectory = 'C:\Program Files\TortoiseSVN\bin',
    [string]$OutputFile = 'LightSvnBrowser.exe',
    [string]$RuntimeArchive = '',
    [switch]$SkipRuntime,
    [switch]$PrepareRuntimeOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectDirectory = Split-Path -Parent $PSScriptRoot
$buildDirectory = Join-Path $projectDirectory 'work\build'
$runtimeDirectory = Join-Path $buildDirectory ('svn-1.14.1-' + [Guid]::NewGuid().ToString('N'))
$runtimeZip = Join-Path $buildDirectory 'SvnRuntime.zip'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if ([string]::IsNullOrWhiteSpace($OutputFile)) { throw 'OutputFile must specify the application EXE path.' }
if ([IO.Path]::IsPathRooted($OutputFile)) {
    $resolvedOutputFile = [IO.Path]::GetFullPath($OutputFile)
} else {
    $resolvedOutputFile = [IO.Path]::GetFullPath((Join-Path $projectDirectory $OutputFile))
}
New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ($SkipRuntime -and -not [string]::IsNullOrWhiteSpace($RuntimeArchive)) {
    throw 'RuntimeArchive cannot be combined with SkipRuntime.'
}
$allowedRuntimeFiles = @('svn.exe', 'libsvn_tsvn.dll', 'libapr_tsvn.dll', 'libaprutil_tsvn.dll', 'libsasl.dll', 'intl3_tsvn.dll',
    'saslANONYMOUS.dll', 'saslCRAMMD5.dll', 'saslDIGESTMD5.dll', 'saslGSSAPI.dll', 'saslLOGIN.dll', 'saslNTLM.dll', 'saslPLAIN.dll',
    'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll',
    'vcruntime140.dll', 'vcruntime140_1.dll', 'concrt140.dll', 'vcomp140.dll', 'vcamp140.dll', 'vccorlib140.dll',
    'Subversion license.txt', 'apr License.txt', 'CyrusSASL License.txt', 'OpenSSL License.txt', 'TortoiseSVN License.txt',
    'THIRD-PARTY-NOTICES.md', 'RUNTIME-SOURCE.txt')
$licenseNames = @('Subversion license.txt', 'apr License.txt', 'CyrusSASL License.txt', 'OpenSSL License.txt', 'TortoiseSVN License.txt')

function Get-PeImports {
    param([string]$FilePath)
    $imageBytes = [IO.File]::ReadAllBytes($FilePath)
    if ($imageBytes.Length -lt 64) { throw "Invalid PE image: $FilePath" }
    $peOffset = [BitConverter]::ToInt32($imageBytes, 60)
    if ($peOffset -lt 0 -or $peOffset + 24 -ge $imageBytes.Length -or
        [BitConverter]::ToUInt32($imageBytes, $peOffset) -ne 0x4550) {
        throw "Invalid PE signature: $FilePath"
    }
    $sectionCount = [BitConverter]::ToUInt16($imageBytes, $peOffset + 6)
    $optionalSize = [BitConverter]::ToUInt16($imageBytes, $peOffset + 20)
    $optionalOffset = $peOffset + 24
    $magic = [BitConverter]::ToUInt16($imageBytes, $optionalOffset)
    if ($magic -eq 0x20b) {
        $directoryOffset = $optionalOffset + 112
        $imageBase = [BitConverter]::ToUInt64($imageBytes, $optionalOffset + 24)
    } elseif ($magic -eq 0x10b) {
        throw "Only x64 SVN binaries are supported: $FilePath"
    } else {
        throw "Unsupported PE optional header: $FilePath"
    }
    $sectionOffset = $optionalOffset + $optionalSize
    $sectionRecords = @()
    for ($sectionIndex = 0; $sectionIndex -lt $sectionCount; $sectionIndex++) {
        $section = $sectionOffset + 40 * $sectionIndex
        $sectionRecords += [pscustomobject]@{
            VirtualSize = [BitConverter]::ToUInt32($imageBytes, $section + 8)
            Address = [BitConverter]::ToUInt32($imageBytes, $section + 12)
            RawSize = [BitConverter]::ToUInt32($imageBytes, $section + 16)
            RawOffset = [BitConverter]::ToUInt32($imageBytes, $section + 20)
        }
    }
    function Convert-RvaToOffset([long]$Rva) {
        foreach ($sectionRecord in $sectionRecords) {
            $extent = [Math]::Max($sectionRecord.VirtualSize, $sectionRecord.RawSize)
            if ($Rva -ge $sectionRecord.Address -and $Rva -lt $sectionRecord.Address + $extent) {
                $fileOffset = $Rva - $sectionRecord.Address + $sectionRecord.RawOffset
                if ($fileOffset -ge 0 -and $fileOffset -lt $imageBytes.Length) { return [int]$fileOffset }
            }
        }
        throw "Invalid PE RVA $Rva in $FilePath"
    }
    function Read-ImportName([long]$NameRva) {
        $nameOffset = Convert-RvaToOffset $NameRva
        $nameEnd = $nameOffset
        while ($nameEnd -lt $imageBytes.Length -and $imageBytes[$nameEnd] -ne 0) { $nameEnd++ }
        if ($nameEnd -ge $imageBytes.Length) { throw "Unterminated PE import: $FilePath" }
        return [Text.Encoding]::ASCII.GetString($imageBytes, $nameOffset, $nameEnd - $nameOffset)
    }
    $importRva = [BitConverter]::ToUInt32($imageBytes, $directoryOffset + 8)
    if ($importRva -ne 0) {
        $descriptor = Convert-RvaToOffset $importRva
        while ($descriptor + 20 -le $imageBytes.Length) {
            $nameRva = [BitConverter]::ToUInt32($imageBytes, $descriptor + 12)
            if ($nameRva -eq 0) { break }
            Read-ImportName $nameRva
            $descriptor += 20
        }
    }
    # Delay-loaded DLLs are dependencies too, even if a quick --version run never touches them.
    $delayRva = [BitConverter]::ToUInt32($imageBytes, $directoryOffset + 13 * 8)
    if ($delayRva -ne 0) {
        $descriptor = Convert-RvaToOffset $delayRva
        while ($descriptor + 32 -le $imageBytes.Length) {
            $attributes = [BitConverter]::ToUInt32($imageBytes, $descriptor)
            $nameRva = [long][BitConverter]::ToUInt32($imageBytes, $descriptor + 4)
            if ($nameRva -eq 0) { break }
            if (($attributes -band 1) -eq 0) { $nameRva -= $imageBase }
            Read-ImportName $nameRva
            $descriptor += 32
        }
    }
}

if (-not $SkipRuntime -and [string]::IsNullOrWhiteSpace($RuntimeArchive)) {
    if (-not [IO.Path]::IsPathRooted($SvnBinDirectory)) { $SvnBinDirectory = Join-Path $projectDirectory $SvnBinDirectory }
    $SvnBinDirectory = [IO.Path]::GetFullPath($SvnBinDirectory)
    $svnExecutable = Join-Path $SvnBinDirectory 'svn.exe'
    if (-not (Test-Path -LiteralPath $svnExecutable -PathType Leaf)) {
        throw "SVN executable not found: $svnExecutable. Supply -SvnBinDirectory or use -SkipRuntime."
    }
    $svnVersion = (& $svnExecutable --version --quiet | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $svnVersion -ne '1.14.1') {
        throw "The current embedded runtime contract supports only SVN 1.14.1 (x64). Detected '$svnVersion' from '$svnExecutable'. Supply a matching -SvnBinDirectory or use -SkipRuntime."
    }
    New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
    $pending = New-Object 'Collections.Generic.Queue[string]'
    $pending.Enqueue('svn.exe')
    # Cyrus SASL loads these providers dynamically; PE imports cannot discover them.
    $saslProviders = @(Get-ChildItem -LiteralPath $SvnBinDirectory -Filter 'sasl*.dll' -File)
    if ($saslProviders.Count -eq 0) { throw 'SASL providers were not found beside svn.exe.' }
    foreach ($provider in $saslProviders) { $pending.Enqueue($provider.Name) }
    $copied = @{}
    $systemDependencies = @{}
    while ($pending.Count -gt 0) {
        $dependencyName = $pending.Dequeue()
        if ($copied.ContainsKey($dependencyName)) { continue }
        if ([IO.Path]::GetFileName($dependencyName) -ne $dependencyName) { throw 'Invalid DLL dependency name.' }
        $sourcePath = Join-Path $SvnBinDirectory $dependencyName
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            $isVcRuntime = $dependencyName -match '^(msvcp|vcruntime|concrt|vcomp|vcamp|vccorlib)\d.*\.dll$'
            if ($isVcRuntime) { throw "Required app-local Microsoft runtime is missing: $dependencyName" }
            $systemFile = Join-Path ([Environment]::SystemDirectory) $dependencyName
            $isApiContract = $dependencyName -match '^(api-ms-|ext-ms-)'
            if ($isApiContract -or (Test-Path -LiteralPath $systemFile -PathType Leaf)) {
                $systemDependencies[$dependencyName] = $true
                continue
            }
            throw "Unresolved native dependency: $dependencyName"
        }
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $runtimeDirectory $dependencyName)
        $copied[$dependencyName] = $true
        foreach ($import in @(Get-PeImports $sourcePath)) { $pending.Enqueue($import) }
    }
    $licenseSource = Split-Path -Parent $SvnBinDirectory
    foreach ($licenseName in $licenseNames) {
        $licensePath = Join-Path $licenseSource $licenseName
        if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) { throw "Missing third-party notice: $licensePath" }
        Copy-Item -LiteralPath $licensePath -Destination (Join-Path $runtimeDirectory $licenseName)
    }
}

if (-not $SkipRuntime -and -not [string]::IsNullOrWhiteSpace($RuntimeArchive)) {
    $archivePath = if ([IO.Path]::IsPathRooted($RuntimeArchive)) {
        [IO.Path]::GetFullPath($RuntimeArchive)
    } else { [IO.Path]::GetFullPath((Join-Path $projectDirectory $RuntimeArchive)) }
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw "RuntimeArchive not found: $archivePath" }
    New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
    $sourceArchive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $seenNames = @{}
        [long]$archiveLength = 0
        foreach ($entry in $sourceArchive.Entries) {
            $name = $entry.FullName
            $flatName = [IO.Path]::GetFileName($name) -eq $name -and $name.IndexOf('/') -lt 0 -and $name.IndexOf('\') -lt 0
            if (-not $flatName -or $allowedRuntimeFiles -notcontains $name -or $seenNames.ContainsKey($name)) {
                throw "RuntimeArchive contains a forbidden, nested, or duplicate entry: $name"
            }
            if ($entry.Length -gt 40MB) { throw "RuntimeArchive entry exceeds the size limit: $name" }
            $archiveLength += $entry.Length
            if ($archiveLength -gt 128MB) { throw 'RuntimeArchive exceeds the expanded size limit.' }
            $seenNames[$name] = $true
        }
        foreach ($requiredFile in @('svn.exe', 'libsvn_tsvn.dll', 'libapr_tsvn.dll', 'libaprutil_tsvn.dll', 'libsasl.dll', 'intl3_tsvn.dll', 'vcruntime140.dll') + $licenseNames) {
            if (-not $seenNames.ContainsKey($requiredFile)) { throw "RuntimeArchive is missing: $requiredFile" }
        }
        foreach ($entry in $sourceArchive.Entries) {
            $canonicalName = @($allowedRuntimeFiles | Where-Object { $_ -ieq $entry.FullName })[0]
            $targetPath = Join-Path $runtimeDirectory $canonicalName
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $targetPath, $false)
        }
    } finally { $sourceArchive.Dispose() }
    $copied = @{}
    $systemDependencies = @{}
    foreach ($nativeFile in @(Get-ChildItem -LiteralPath $runtimeDirectory -File | Where-Object { $_.Extension -in @('.dll', '.exe') })) {
        $copied[$nativeFile.Name] = $true
        foreach ($dependencyName in @(Get-PeImports $nativeFile.FullName)) {
            if (Test-Path -LiteralPath (Join-Path $runtimeDirectory $dependencyName) -PathType Leaf) { continue }
            if ($dependencyName -match '^(msvcp|vcruntime|concrt|vcomp|vcamp|vccorlib)\d.*\.dll$') {
                throw "RuntimeArchive is missing app-local Microsoft dependency: $dependencyName"
            }
            if ($dependencyName -match '^(api-ms-|ext-ms-)' -or
                (Test-Path -LiteralPath (Join-Path ([Environment]::SystemDirectory) $dependencyName) -PathType Leaf)) {
                $systemDependencies[$dependencyName] = $true
            } else { throw "RuntimeArchive has unresolved dependency: $dependencyName" }
        }
    }
    if (@(Get-ChildItem -LiteralPath $runtimeDirectory -Filter 'sasl*.dll' -File).Count -eq 0) {
        throw 'RuntimeArchive is missing its SASL authentication providers.'
    }
    $archiveSvn = Join-Path $runtimeDirectory 'svn.exe'
    $archiveVersion = (& $archiveSvn --version --quiet | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $archiveVersion -ne '1.14.1') {
        throw "The current embedded runtime contract supports only SVN 1.14.1 (x64). RuntimeArchive contains '$archiveVersion'."
    }
}

if (-not $SkipRuntime) {
    foreach ($payloadFile in @(Get-ChildItem -LiteralPath $runtimeDirectory -File)) {
        if ($allowedRuntimeFiles -notcontains $payloadFile.Name) { throw "Unsupported runtime payload: $($payloadFile.Name)" }
    }
    $noticeFile = Join-Path $projectDirectory 'THIRD-PARTY-NOTICES.md'
    Copy-Item -LiteralPath $noticeFile -Destination (Join-Path $runtimeDirectory 'THIRD-PARTY-NOTICES.md')
    $provenance = @(
        'SVN runtime: 1.14.1 (x64), unmodified TortoiseSVN command-line distribution.',
        'Source project: https://tortoisesvn.net/ ; https://subversion.apache.org/',
        'Build input: a locally supplied runtime; no download or installer is used.',
        'Collected native imports and dynamic SASL providers:',
        ($copied.Keys | Sort-Object | Out-String).Trim(),
        'Operating-system dependencies (not redistributed):',
        ($systemDependencies.Keys | Sort-Object | Out-String).Trim()
    ) -join "`r`n"
    [IO.File]::WriteAllText((Join-Path $runtimeDirectory 'RUNTIME-SOURCE.txt'), $provenance, (New-Object Text.UTF8Encoding($false)))
    $zipStream = [IO.File]::Open($runtimeZip, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($payload in @(Get-ChildItem -LiteralPath $runtimeDirectory -File | Sort-Object Name)) {
            $entry = $archive.CreateEntry($payload.Name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $inputStream = [IO.File]::OpenRead($payload.FullName)
            $entryStream = $entry.Open()
            try { $inputStream.CopyTo($entryStream) } finally { $entryStream.Dispose(); $inputStream.Dispose() }
        }
    } finally { $archive.Dispose(); $zipStream.Dispose() }
    $unpackedSize = (Get-ChildItem -LiteralPath $runtimeDirectory -File | Measure-Object Length -Sum).Sum
    Write-Output ("Embedded SVN: {0} files, {1:N2} MiB unpacked, {2:N2} MiB compressed." -f
        (Get-ChildItem -LiteralPath $runtimeDirectory -File).Count, ($unpackedSize / 1MB), ((Get-Item -LiteralPath $runtimeZip).Length / 1MB))
    Write-Output ('Prepared runtime archive: ' + $runtimeZip)
}

if ($PrepareRuntimeOnly) { return }
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($resolvedOutputFile)) -Force | Out-Null
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "C# compiler not found: $compiler" }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'src') -Filter '*.cs' -File -Recurse | Sort-Object FullName | ForEach-Object FullName)
if ($sources.Count -eq 0) { throw 'No C# source files found in src.' }
$iconFile = Join-Path $projectDirectory 'assets\app.ico'
if (-not (Test-Path -LiteralPath $iconFile -PathType Leaf)) { throw "Application icon not found: $iconFile" }
$compilerArguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/utf8output', '/codepage:65001',
    ('/out:' + $resolvedOutputFile), ('/win32manifest:' + (Join-Path $projectDirectory 'app.manifest')), ('/win32icon:' + $iconFile),
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Runtime.Serialization.dll',
    '/reference:System.Security.dll', '/reference:System.Xml.Linq.dll', '/reference:System.IO.Compression.dll',
    '/reference:System.IO.Compression.FileSystem.dll', '/reference:System.Core.dll', '/reference:System.Xml.dll')
if (-not $SkipRuntime) { $compilerArguments += '/resource:' + $runtimeZip + ',LightSvnBrowser.SvnRuntime.zip' }
$previousCompilerTemp = $env:TEMP
$previousCompilerTmp = $env:TMP
try {
    $env:TEMP = $buildDirectory
    $env:TMP = $buildDirectory
    & $compiler @compilerArguments @sources
    if ($LASTEXITCODE -ne 0) { throw "C# compiler failed with exit code $LASTEXITCODE." }
} finally {
    $env:TEMP = $previousCompilerTemp
    $env:TMP = $previousCompilerTmp
}
Write-Output ("Built: {0} ({1:N2} MiB)" -f $resolvedOutputFile, ((Get-Item -LiteralPath $resolvedOutputFile).Length / 1MB))
