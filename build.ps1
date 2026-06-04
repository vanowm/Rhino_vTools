Set-Location d:\github\rhino\vTools

# Bump CalVer in csproj and AssemblyInfo: YY.M.D.Hmm (no leading zeros)
$cur = (Select-String -Path vTools.csproj -Pattern '<Version>([^<]+)').Matches[0].Groups[1].Value
$v   = Get-Date -Format 'yy.M.d.Hmm'
(Get-Content vTools.csproj)             -replace [regex]::Escape($cur), $v | Set-Content vTools.csproj
(Get-Content Properties\AssemblyInfo.cs) -replace [regex]::Escape($cur), $v | Set-Content Properties\AssemblyInfo.cs

# Require a pending message file before building
$pendingFile = '.git\vtools-pending-message.txt'
if (-not (Test-Path $pendingFile)) {
    Write-Host "ERROR: Missing $pendingFile - write a descriptive commit message there first." -ForegroundColor Red
    exit 1
}

# Build
dotnet build vTools.csproj -c Release --no-incremental
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
