@echo off
echo === Building TADOFAI.Mod ===
msbuild TADOFAI\TADOFAI.Mod.csproj /p:Configuration=Release /p:Platform="AnyCPU"
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%

echo === Building TADOFAI.Loader.UMM ===
msbuild TADOFAI.Loader.UMM\TADOFAI.Loader.UMM.csproj /p:Configuration=Release /p:Platform="AnyCPU"
if %ERRORLEVEL% neq 0 exit /b %ERRORLEVEL%

echo === Packaging ===
powershell -NoProfile -Command "Compress-Archive -Path 'TADOFAI.Loader.UMM\bin\Release\TADOFAI.Loader.UMM.dll','TADOFAI.Loader.UMM\bin\Release\TADOFAI.Mod.dll','TADOFAI.Loader.UMM\bin\Release\info.json' -DestinationPath 'TADOFAI.Mod.zip' -Force"

echo OK