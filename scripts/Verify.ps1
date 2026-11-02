[CmdletBinding()]
param(
    [string]$SvnExecutable = 'C:\Program Files\TortoiseSVN\bin\svn.exe',
    [string]$ApplicationExecutable = 'LightSvnBrowser.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectDirectory = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ApplicationExecutable)) { throw 'ApplicationExecutable must specify the application EXE path.' }
if ([IO.Path]::IsPathRooted($ApplicationExecutable)) {
    $appExe = [IO.Path]::GetFullPath($ApplicationExecutable)
} else {
    $appExe = [IO.Path]::GetFullPath((Join-Path $projectDirectory $ApplicationExecutable))
}
$verificationDirectory = Join-Path $projectDirectory 'work\verification'
New-Item -ItemType Directory -Path $verificationDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "C# compiler missing: $compiler" }
if (-not (Test-Path -LiteralPath $SvnExecutable -PathType Leaf)) { throw "SVN client missing: $SvnExecutable" }
$references = @('/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    '/reference:System.Runtime.Serialization.dll', '/reference:System.Security.dll',
    '/reference:System.Xml.Linq.dll', '/reference:System.IO.Compression.dll',
    '/reference:System.IO.Compression.FileSystem.dll', '/reference:System.Core.dll', '/reference:System.Xml.dll')
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'src') -Filter '*.cs' -File | ForEach-Object FullName)
$testCases = @(
    @{ Name='CoreTests'; Main='TestRunner'; Source='tests\CoreTests.cs'; Arguments=@((Join-Path $verificationDirectory 'core-data'), $SvnExecutable) },
    @{ Name='UiTests'; Main='LightSvnBrowser.UiTests'; Source='tests\UiTests.cs'; Arguments=@((Join-Path $verificationDirectory 'ui-data')) }
)
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
try {
    $env:TEMP = $verificationDirectory
    $env:TMP = $verificationDirectory
    if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) { throw "Build the application before verifying it: $appExe" }
    $smokeDirectory = Join-Path $verificationDirectory ('smoke-data\' + [Guid]::NewGuid().ToString('N'))
    $screenshot = Join-Path $verificationDirectory 'main-window.png'
    # Use correctly quoted paths, including projects stored below directories containing spaces.
    $smokeArguments = '--data-dir "' + $smokeDirectory + '" --smoke-test "' + $screenshot + '"'
    $process = Start-Process -FilePath $appExe -ArgumentList $smokeArguments -WorkingDirectory $projectDirectory -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        throw 'Packaged EXE smoke test timed out.'
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $screenshot -PathType Leaf)) {
        $errorFile = Join-Path $smokeDirectory 'smoke-error.txt'
        if (Test-Path -LiteralPath $errorFile) { Get-Content -LiteralPath $errorFile | Write-Output }
        throw 'Packaged EXE smoke test failed.'
    }
    $bundledSvn = Join-Path $smokeDirectory 'runtime\svn-1.14.1\svn.exe'
    if (Test-Path -LiteralPath $bundledSvn -PathType Leaf) {
        $bundledVersion = & $bundledSvn --version --quiet
        if ($LASTEXITCODE -ne 0) { throw 'Embedded SVN client failed to start.' }
        Write-Output ('PASS: packaged EXE opens, renders, and extracts its SVN client (' + $bundledVersion + ').')
    } else {
        Write-Output 'PASS: EXE opens and renders (external SVN build).'
    }
    foreach ($testCase in $testCases) {
        $outputExe = Join-Path $verificationDirectory ($testCase.Name + '.exe')
        $compilerOptions = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/codepage:65001',
            ('/main:' + $testCase.Main), ('/out:' + $outputExe)) + $references
        & $compiler @compilerOptions @sources (Join-Path $projectDirectory $testCase.Source)
        if ($LASTEXITCODE -ne 0) { throw ($testCase.Name + ' compilation failed.') }
        $testArguments = @($testCase.Arguments)
        if ($testCase.Name -eq 'CoreTests' -and (Test-Path -LiteralPath $bundledSvn -PathType Leaf)) { $testArguments += $bundledSvn }
        $result = @(& $outputExe @testArguments)
        $testExitCode = $LASTEXITCODE
        $result | Set-Content -LiteralPath (Join-Path $verificationDirectory ($testCase.Name + '.log')) -Encoding UTF8
        $result | Write-Output
        if ($testExitCode -ne 0) { throw ($testCase.Name + ' failed.') }
    }
    Write-Output ('Screenshot: ' + $screenshot)
} finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
}
