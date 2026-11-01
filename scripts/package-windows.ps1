param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'publish\EmberKit-Windows-x64-Portable'),
    [string]$WorkshopListPath
)

$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
$archive = $output + '.zip'
# Always package a fresh publish directory, never an existing installation with personal data.
if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $archive)) {
    throw 'Output directory or ZIP already exists. Choose a new OutputDirectory.'
}
if ($WorkshopListPath -and -not (Test-Path -LiteralPath $WorkshopListPath -PathType Leaf)) {
    throw 'Workshop TXT list does not exist.'
}
dotnet publish (Join-Path $project 'FireAxe.GUI\FireAxe.GUI.csproj') -c Release -r win-x64 --self-contained true -o $output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Move-Item -LiteralPath (Join-Path $output 'FireAxe.exe') -Destination (Join-Path $output 'EmberKit.exe')
foreach ($name in @('LICENSE.txt', 'NOTICE.txt', 'README.md', 'README.zh-Hans.md', 'MODIFICATIONS.md')) {
    Copy-Item -LiteralPath (Join-Path $project $name) -Destination $output -Force
}
$docs = Join-Path $output 'docs'
New-Item -ItemType Directory -Path $docs -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $project 'docs\usage.zh-Hans.md') -Destination $docs -Force
New-Item -ItemType Directory -Path (Join-Path $output 'data') | Out-Null
if ($WorkshopListPath) {
    Copy-Item -LiteralPath $WorkshopListPath -Destination (Join-Path $output ([IO.Path]::GetFileName($WorkshopListPath)))
}
[IO.Compression.ZipFile]::CreateFromDirectory($output, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
Write-Output "Ready: $output\EmberKit.exe"
Write-Output "ZIP: $archive"
