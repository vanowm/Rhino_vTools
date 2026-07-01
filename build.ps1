Set-Location d:\github\rhino\vTools

# Version is auto-computed at build time via $(BuildVersion) in vTools.csproj.
# Do NOT modify vTools.csproj or Properties\AssemblyInfo.cs here.

$pendingFile = '.git\vtools-pending-message.txt'

function Get-Label([string]$name) {
    if ($name -eq 'vTogglePerpGumball') { return 'PerpGumbal' }
    if ($name.StartsWith('v') -and $name.Length -gt 1) { return $name.Substring(1) }
    return $name
}

function Build-LabelList([System.Collections.Generic.List[string]]$items) {
    if ($items.Count -eq 0) { return '' }
    $labels = New-Object System.Collections.Generic.List[string]
    foreach ($i in $items) { [void]$labels.Add((Get-Label $i)) }
    if ($labels.Count -le 2) { return ($labels -join ', ') }
    return (($labels[0..1] -join ', ') + ', +' + ($labels.Count - 2) + ' more')
}

if (-not (Test-Path $pendingFile)) {
    $changes = @()
    git diff --name-status -- . | ForEach-Object {
        $parts = $_ -split '\t+', 2
        if ($parts.Count -ge 2) {
            $changes += [pscustomobject]@{ Status = $parts[0]; Path = $parts[1] }
        }
    }

    $cmdAdds   = New-Object System.Collections.Generic.List[string]
    $cmdMods   = New-Object System.Collections.Generic.List[string]
    $hasReadme = $false
    $hasBuildCfg = $false
    $hasDll    = $false

    foreach ($c in $changes) {
        $p = ($c.Path -replace '\\','/')
        if ($p -eq 'README.md') { $hasReadme = $true }
        if ($p -eq 'vTools.csproj' -or $p -eq 'Properties/AssemblyInfo.cs') { $hasBuildCfg = $true }
        if ($p -eq 'bin/Release/net7.0-windows/vTools.dll') { $hasDll = $true }
        if ($p -like 'Commands/*.cs') {
            $n = [System.IO.Path]::GetFileNameWithoutExtension($p)
            if ($n -like 'v*') {
                if ($c.Status -like 'A*') {
                    if (-not $cmdAdds.Contains($n)) { [void]$cmdAdds.Add($n) }
                } else {
                    if (-not $cmdMods.Contains($n)) { [void]$cmdMods.Add($n) }
                }
            }
        }
    }

    $parts = New-Object System.Collections.Generic.List[string]
    if ($cmdAdds.Count -eq 1) { $parts.Add('add ' + (Get-Label $cmdAdds[0]) + ' command') }
    elseif ($cmdAdds.Count -gt 1) { $parts.Add('add commands: ' + (Build-LabelList $cmdAdds)) }
    if ($cmdMods.Count -eq 1) { $label = Get-Label $cmdMods[0]; $parts.Add($label + ': update') }
    elseif ($cmdMods.Count -gt 1) { $parts.Add('update: ' + (Build-LabelList $cmdMods)) }
    if ($hasReadme) { $parts.Add('docs: refresh command notes') }
    if ($hasBuildCfg -and $parts.Count -eq 0) { $parts.Add('build: sync version metadata') }
    if ($hasDll -and $parts.Count -eq 0) { $parts.Add('build: publish release binary') }
    if ($parts.Count -eq 0) { $parts.Add('maintenance: apply project updates') }

    $summary = ($parts -join '; ')
    Set-Content -Path $pendingFile -Value $summary -NoNewline -Encoding utf8
    Write-Host "Created pending message file: $pendingFile -> $summary" -ForegroundColor Green
}

# Build
$buildOutput = dotnet build vTools.csproj -c Release --no-incremental 2>&1
$buildExitCode = $LASTEXITCODE
if ($buildExitCode -ne 0) {
    if ($buildOutput -match 'being used by another process' -or $buildOutput -match 'cannot access the file' -or $buildOutput -match 'Cannot write file') {
        Write-Host "WARNING: vTools build reported a locked DLL; prebuild is considered successful and the pending commit message file has already been created." -ForegroundColor Yellow
    } else {
        Write-Host $buildOutput
        exit $buildExitCode
    }
}
