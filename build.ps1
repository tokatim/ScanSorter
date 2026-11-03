$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/ScanSorter/ScanSorter.csproj'
$output = Join-Path $PSScriptRoot 'publish'
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }