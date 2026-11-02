[CmdletBinding()]
param(
    [string]$SvnBinDirectory = 'C:\Program Files\TortoiseSVN\bin',
    [string]$RuntimeArchive = '',
    [string]$SvnExecutable = '',
    [switch]$SkipRuntime,
    [switch]$SkipVerify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectDirectory = Split-Path -Parent $PSScriptRoot
$versionSource = [IO.File]::ReadAllText((Join-Path $projectDirectory 'src\AssemblyInfo.cs'))
$versionMatch = [regex]::Match($versionSource, 'AssemblyInformationalVersion\("([0-9]+\.[0-9]+\.[0-9]+)"\)')
if (-not $versionMatch.Success) { throw 'AssemblyInfo.cs must contain a three-part release version.' }
$version = $versionMatch.Groups[1].Value
$packageWork = Join-Path $projectDirectory ('work\package\' + [Guid]::NewGuid().ToString('N'))
$stageDirectory = Join-Path $packageWork 'payload'
$distDirectory = Join-Path $projectDirectory 'dist'
New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $distDirectory -Force | Out-Null
$applicationExecutable = Join-Path $stageDirectory 'LightSvnBrowser.exe'
$buildOptions = @{ SvnBinDirectory = $SvnBinDirectory; OutputFile = $applicationExecutable; SkipRuntime = $SkipRuntime }
if (-not [string]::IsNullOrWhiteSpace($RuntimeArchive)) { $buildOptions.RuntimeArchive = $RuntimeArchive }
& (Join-Path $PSScriptRoot 'Build.ps1') @buildOptions

if (-not $SkipVerify) {
    if ([string]::IsNullOrWhiteSpace($SvnExecutable)) { $SvnExecutable = Join-Path $SvnBinDirectory 'svn.exe' }
    if (-not [IO.Path]::IsPathRooted($SvnExecutable)) { $SvnExecutable = Join-Path $projectDirectory $SvnExecutable }
    & (Join-Path $PSScriptRoot 'Verify.ps1') -ApplicationExecutable $applicationExecutable -SvnExecutable $SvnExecutable
} else {
    Write-Warning 'Verification was explicitly skipped. The output archive has not passed this packaging run''s checks.'
}

# Package only these reviewed, static files. Never copy a source directory or application data tree.
$releaseEntries = @{}
$releaseEntries['LightSvnBrowser.exe'] = $applicationExecutable
$releaseDocuments = @(
    'README.md',
    'THIRD-PARTY-NOTICES.md',
    'docs/TROUBLESHOOTING.md',
    'docs/RELEASE-NOTES.md',
    'docs/初版验证与改动说明.md',
    'docs/20261102-SVN证书主机名不匹配排查.md',
    'docs/20261102-IP仓库兼容与真实访问验证.md'
)
foreach ($documentName in $releaseDocuments) {
    $documentPath = Join-Path $projectDirectory $documentName
    if (-not (Test-Path -LiteralPath $documentPath -PathType Leaf)) { throw "Release document missing: $documentName" }
    $releaseEntries[$documentName] = $documentPath
}
$previewName = 'docs/preview.png'
$previewPath = Join-Path $projectDirectory $previewName
if (Test-Path -LiteralPath $previewPath -PathType Leaf) { $releaseEntries[$previewName] = $previewPath }
$licensePath = Join-Path $projectDirectory 'LICENSE'
if (Test-Path -LiteralPath $licensePath -PathType Leaf) { $releaseEntries['LICENSE'] = $licensePath }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not $SkipRuntime) {
    $runtimeZip = Join-Path $projectDirectory 'work\build\SvnRuntime.zip'
    $runtimeArchiveReader = [IO.Compression.ZipFile]::OpenRead($runtimeZip)
    try {
        $licenseDirectory = Join-Path $stageDirectory 'licenses'
        New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
        foreach ($licenseName in @('Subversion license.txt', 'apr License.txt', 'CyrusSASL License.txt', 'OpenSSL License.txt', 'TortoiseSVN License.txt')) {
            $entry = $runtimeArchiveReader.GetEntry($licenseName)
            if ($null -eq $entry) { throw "Runtime license missing from prepared archive: $licenseName" }
            $destination = Join-Path $licenseDirectory $licenseName
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
            $releaseEntries['licenses/' + $licenseName] = $destination
        }
    } finally { $runtimeArchiveReader.Dispose() }
}
$packageSuffix = if ($SkipRuntime) { '-external-svn' } else { '' }
$archiveName = 'LightSvnBrowser-' + $version + '-win-x64' + $packageSuffix + '.zip'
$archivePath = Join-Path $distDirectory $archiveName
$zipStream = [IO.File]::Open($archivePath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$releaseArchive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
try {
    foreach ($entryName in @($releaseEntries.Keys | Sort-Object)) {
        $entry = $releaseArchive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $inputStream = [IO.File]::OpenRead($releaseEntries[$entryName])
        $entryStream = $entry.Open()
        try { $inputStream.CopyTo($entryStream) } finally { $entryStream.Dispose(); $inputStream.Dispose() }
    }
} finally { $releaseArchive.Dispose(); $zipStream.Dispose() }
Write-Output ("Packaged: {0} ({1:N2} MiB)" -f $archivePath, ((Get-Item -LiteralPath $archivePath).Length / 1MB))
Write-Output ('Included entries: ' + (($releaseEntries.Keys | Sort-Object) -join ', '))
