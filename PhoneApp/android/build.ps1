$ErrorActionPreference='Stop'
$projectRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$tools=$env:YANJIU_BUILD_TOOLS
if([string]::IsNullOrWhiteSpace($tools)){$tools=Join-Path $PSScriptRoot '../local/tools'}
$artifact=Join-Path $PSScriptRoot '../local/artifacts'
New-Item -ItemType Directory -Force -Path $artifact | Out-Null
$jdk=(Get-ChildItem "$tools/jdk" -Directory | Select-Object -First 1).FullName
$buildTools=(Get-ChildItem "$tools/build-tools" -Directory | Select-Object -First 1).FullName
$androidJar=Join-Path $tools 'platform/android-35/android.jar'
$build=Join-Path ([IO.Path]::GetTempPath()) 'ff14chat-build'
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$build=[IO.Path]::GetFullPath($build)
if(-not $build.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($build) -ne 'ff14chat-build'){throw 'Unexpected build directory'}
if(Test-Path -LiteralPath $build){Remove-Item -LiteralPath $build -Recurse -Force}
New-Item -ItemType Directory -Force -Path "$build/classes","$build/dex" | Out-Null
Copy-Item -LiteralPath $androidJar -Destination "$build/android.jar" -Force
$androidJar="$build/android.jar"
Copy-Item -LiteralPath "$PSScriptRoot/AndroidManifest.xml" -Destination "$build/AndroidManifest.xml" -Force
Copy-Item -LiteralPath "$PSScriptRoot/assets" -Destination "$build" -Recurse -Force
Copy-Item -LiteralPath "$PSScriptRoot/res" -Destination "$build" -Recurse -Force
$sources=Get-ChildItem "$PSScriptRoot/java" -Recurse -Filter '*.java' | ForEach-Object { '"'+$_.FullName.Replace('\','/')+'"' }
$sources | Set-Content "$build/java-sources.txt" -Encoding utf8NoBOM
function Check([string]$step){if($LASTEXITCODE -ne 0){throw "$step failed ($LASTEXITCODE)"}}
& "$jdk/bin/javac.exe" '-J-Dfile.encoding=UTF-8' -encoding UTF-8 -source 8 -target 8 -classpath "$androidJar;$tools/zxing.jar" -d "$build/classes" "@$build/java-sources.txt";Check 'javac'
Push-Location "$build/classes"
try{& "$jdk/bin/jar.exe" cf "$build/classes.jar" .;Check 'jar'}finally{Pop-Location}
& "$jdk/bin/java.exe" -cp "$buildTools/lib/d8.jar" com.android.tools.r8.D8 --min-api 26 --lib $androidJar --output "$build/dex" "$build/classes.jar" "$tools/zxing.jar";Check 'd8'
& "$buildTools/aapt2.exe" compile --dir "$build/res" -o "$build/resources.zip";Check 'resources'
& "$buildTools/aapt2.exe" link -o "$build/unsigned.apk" --manifest "$build/AndroidManifest.xml" -I $androidJar -A "$build/assets" "$build/resources.zip" --min-sdk-version 26 --target-sdk-version 35;Check 'aapt2'
Push-Location "$build/dex"
try{& "$jdk/bin/jar.exe" uf "$build/unsigned.apk" classes.dex;Check 'dex package'}finally{Pop-Location}
& "$buildTools/zipalign.exe" -f 4 "$build/unsigned.apk" "$build/aligned.apk";Check 'zipalign'
$key=$env:YANJIU_KEYSTORE
if([string]::IsNullOrWhiteSpace($key)){
 Copy-Item -LiteralPath "$build/aligned.apk" -Destination (Join-Path $artifact '小烟酒-unsigned.apk') -Force
 Write-Output "Unsigned APK: $artifact/小烟酒-unsigned.apk"
}else{
 if(!(Test-Path -LiteralPath $key)){throw 'Signing keystore not found'}
 if([string]::IsNullOrWhiteSpace($env:YANJIU_KEYSTORE_PASSWORD) -or [string]::IsNullOrWhiteSpace($env:YANJIU_KEY_ALIAS)){throw 'Set signing password and alias environment variables'}
 if([string]::IsNullOrWhiteSpace($env:YANJIU_KEY_PASSWORD)){$env:YANJIU_KEY_PASSWORD=$env:YANJIU_KEYSTORE_PASSWORD}
 $apk=Join-Path $artifact '小烟酒.apk'
 & "$jdk/bin/java.exe" -jar "$buildTools/lib/apksigner.jar" sign --ks $key --ks-key-alias $env:YANJIU_KEY_ALIAS --ks-pass env:YANJIU_KEYSTORE_PASSWORD --key-pass env:YANJIU_KEY_PASSWORD --out $apk "$build/aligned.apk";Check 'APK sign'
 & "$jdk/bin/java.exe" -jar "$buildTools/lib/apksigner.jar" verify --verbose $apk;Check 'APK verify'
 Write-Output "APK: $apk"
}
