param(
    [string]$OmniLibPath = '',
    [string]$DalamudLibPath = '',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$OmniLibPath) {
    $pluginRoot = Join-Path $env:APPDATA 'XIVLauncherCN/installedPlugins/OmniToolbox'
    $OmniLibPath = (Get-ChildItem -LiteralPath $pluginRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'OmniToolbox.Common.dll') } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName
}
if (!$DalamudLibPath) {
    $hooksRoot = Join-Path $env:APPDATA 'XIVLauncherCN/addon/Hooks'
    $DalamudLibPath = (Get-ChildItem -LiteralPath $hooksRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Dalamud.dll') } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
foreach ($path in @($OmniLibPath, $DalamudLibPath)) {
    if (!$path -or !(Test-Path -LiteralPath $path)) { throw "Dependency directory missing: $path" }
}
$references = @{}
foreach ($folder in @($OmniLibPath, $DalamudLibPath)) {
    Get-ChildItem -LiteralPath $folder -File -Filter '*.dll' | ForEach-Object {
        if (!$references.ContainsKey($_.BaseName)) {
            try {
                $null = [Reflection.AssemblyName]::GetAssemblyName($_.FullName)
                $references[$_.BaseName] = $_.FullName
            } catch { } # Native DLLs are not C# references.
        }
    }
}
$referenceXml = ($references.GetEnumerator() | Sort-Object Key | ForEach-Object {
    $escaped = [Security.SecurityElement]::Escape($_.Value)
    "<Reference Include=`"$($_.Key)`"><HintPath>$escaped</HintPath><Private>false</Private></Reference>"
}) -join "`n"
$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'TreeHouseModules.json') -Raw -Encoding utf8 | ConvertFrom-Json
$output = Join-Path $repoRoot '.artifacts/api1'
foreach ($module in $manifest.Modules) {
    $name = $module.InternalName
    $source = if ($name -eq 'GameWebBrowser') { 'Modules/GameWebBrowser.cs' } else { $module.File }
    $source = [Security.SecurityElement]::Escape((Join-Path $repoRoot $source))
    $include = "<Compile Include=`"$source`" />"
    if ($name -eq 'GameWebBrowser') {
        $extra = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'Browser/Module/CefIntegration.cs'))
        $include += "<Compile Include=`"$extra`" />"
    }
    $folder = Join-Path $output $name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework><UseWindowsForms>true</UseWindowsForms>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>preview</LangVersion><EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AssemblyName>$name</AssemblyName><Version>$($module.Version)</Version>
  </PropertyGroup>
  <ItemGroup>$include$referenceXml<Using Remove="System.Windows.Forms" /></ItemGroup>
</Project>
"@
    $projectPath = Join-Path $folder "$name.csproj"
    [IO.File]::WriteAllText($projectPath, $project, [Text.UTF8Encoding]::new($false))
    & $Dotnet build $projectPath -c Release --nologo
    if ($LASTEXITCODE) { throw "Build failed: $name" }
}
Write-Host "Built all API 1 modules in $output. Dependencies are references only; publish only each module's own file."
