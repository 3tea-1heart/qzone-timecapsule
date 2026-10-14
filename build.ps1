param([string]$OutputRoot = 'release')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools/dotnet'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:LIB = ''
[xml]$project = Get-Content src/QZoneArchive/QZoneArchive.csproj
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version' }
$output = Join-Path $OutputRoot "TimeCapsule-$version"
dotnet restore src/QZoneArchive -r win-x64 --configfile NuGet.Config -p:PublishSingleFile=true -p:SelfContained=true
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
dotnet publish src/QZoneArchive -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item docs/使用说明.md,docs/THIRD-PARTY-NOTICES.md,docs/QZoneExport-APACHE-2.0.txt $output -Force
Copy-Item "docs/更新说明-$version.md" $output -Force
$assets = Get-Content src/QZoneArchive/obj/project.assets.json -Raw | ConvertFrom-Json
$licenses = @{
    'microsoft.web.webview2' = @('LICENSE.txt','WebView2-LICENSE.txt')
    'microsoft.netcore.app.runtime.win-x64' = @('LICENSE.TXT','DotNet-LICENSE.txt')
    'microsoft.windowsdesktop.app.runtime.win-x64' = @('LICENSE','WindowsDesktop-LICENSE.txt')
}
foreach ($package in $licenses.Keys) {
    $entry = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name.ToLowerInvariant().StartsWith("$package/") })
    if ($entry.Count -eq 1) { $packagePath = $entry[0].Value.path }
    else {
        $downloads = @($assets.project.frameworks.PSObject.Properties | ForEach-Object { $_.Value.downloadDependencies } | Where-Object { $_.name -ieq $package })
        if ($downloads.Count -ne 1 -or $downloads[0].version -notmatch '^\[([^,]+),\s*\1\]$') { throw "Cannot resolve license: $package" }
        $packagePath = "$package/$($Matches[1])"
    }
    $source = Join-Path $env:NUGET_PACKAGES ($packagePath + '/' + $licenses[$package][0])
    Copy-Item $source (Join-Path $output $licenses[$package][1]) -Force
}
$zip = Join-Path $OutputRoot "TimeCapsule-$version-Windows-x64.zip"
Compress-Archive -Path "$output/*" -DestinationPath $zip -Force
$hash = Get-FileHash $zip -Algorithm SHA256
"$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($zip))" | Set-Content "$zip.sha256" -Encoding ascii
Write-Output "Built $zip"
