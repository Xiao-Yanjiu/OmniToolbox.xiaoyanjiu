# SPDX-License-Identifier: GPL-3.0-or-later
# Run in the distributed 第三方许可/Lagrange-V1 folder. Requires .NET 9 SDK and Python.
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
 if(!(Test-Path -LiteralPath 'source/Lagrange.Core-1')){Expand-Archive -LiteralPath 'Lagrange-V1-source.zip' -DestinationPath 'source'}
 $core=Join-Path $PSScriptRoot 'source/Lagrange.Core-1/Lagrange.Core/Lagrange.Core.csproj'
 & dotnet build $core -c Release -p:TargetFramework=net9.0 -p:GeneratePackageOnBuild=false
 if($LASTEXITCODE -ne 0){throw 'Core build failed'}
 @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><AssemblyName>PhoneQQBridge</AssemblyName><EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>enable</ImplicitUsings><Nullable>disable</Nullable></PropertyGroup><ItemGroup><Compile Include="PhoneQQBridge.cs"/><Reference Include="Lagrange.Core"><HintPath>source/Lagrange.Core-1/Lagrange.Core/bin/Release/net9.0/Lagrange.Core.dll</HintPath></Reference><PackageReference Include="protobuf-net" Version="3.2.16"/></ItemGroup></Project>
'@ | Set-Content -LiteralPath 'PhoneQQBridge.csproj' -Encoding utf8
 & dotnet build 'PhoneQQBridge.csproj' -c Release
 if($LASTEXITCODE -ne 0){throw 'Bridge build failed'}
 & python '重建载荷.py'
 if($LASTEXITCODE -ne 0){throw 'Embedding failed'}
 '已生成 QQ载荷.txt；将内容替换到 PhoneChat.cs 中 QQAssemblyContext 的 blobs 字典初始化项。'
} finally {Pop-Location}
