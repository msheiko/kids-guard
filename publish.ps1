# Builds tests and publishes self-contained single-file executables into .\publish
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'publish'

dotnet test (Join-Path $root 'KidGuard.slnx') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

foreach ($project in 'src/KidGuard.Service/KidGuard.Service.csproj', 'src/KidGuard.Tray/KidGuard.Tray.csproj') {
    dotnet publish (Join-Path $root $project) -c Release -o $out
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}

Get-ChildItem $out -Filter *.pdb | Remove-Item
Write-Host "Done: $out"
Get-ChildItem $out -Filter KidGuard.*.exe | Format-Table Name, Length
