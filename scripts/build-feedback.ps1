param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$RunChecks
)
$ErrorActionPreference = 'Stop'
$nativeProject = Join-Path (Split-Path $PSScriptRoot -Parent) 'native\EmberFeedback'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ Build Tools (v143, x86, Windows 10 SDK) are required.' }
$buildEngine = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $buildEngine) { throw 'Visual Studio C++ Build Tools (v143, x86, Windows 10 SDK) are required.' }
& $buildEngine (Join-Path $nativeProject 'EmberFeedback.vcxproj') /nologo /verbosity:minimal /m /p:Platform=Win32 "/p:Configuration=$Configuration"
if ($LASTEXITCODE -ne 0) { throw 'EmberFeedback native build failed.' }
& $buildEngine (Join-Path $nativeProject 'EmberFeedback.Loader.vcxproj') /nologo /verbosity:minimal /m /p:Platform=Win32 "/p:Configuration=$Configuration"
if ($LASTEXITCODE -ne 0) { throw 'EmberFeedback client loader build failed.' }
if ($RunChecks) {
    foreach ($fixtureConfiguration in @('Host', 'Module')) {
        & $buildEngine (Join-Path $nativeProject 'EmberFeedback.Fixture.vcxproj') /nologo /verbosity:minimal /p:Platform=Win32 "/p:Configuration=$fixtureConfiguration"
        if ($LASTEXITCODE -ne 0) { throw 'Isolated loader fixture build failed.' }
    }
    & $buildEngine (Join-Path $nativeProject 'EmberFeedback.Checks.vcxproj') /nologo /verbosity:minimal /p:Platform=Win32 /p:Configuration=Release
    if ($LASTEXITCODE -ne 0) { throw 'Native checks build failed.' }
    & (Join-Path $nativeProject 'bin\Checks\EmberFeedback.Checks.exe') (Join-Path $nativeProject 'assets') (Join-Path $nativeProject 'bin\Fixtures')
    if ($LASTEXITCODE -ne 0) { throw 'Native checks failed.' }
}
