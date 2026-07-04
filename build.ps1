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
# Protect the runtime config next to the DLL: vTools.csproj copies the project-root
# vTools.config.json to the output with PreserveNewest, which can overwrite settings
# the user saved at runtime. Snapshot before build and restore if overwritten.
$configSrc  = 'vTools.config.json'                                        # project root (build source)
$configDst  = 'bin\Release\net7.0-windows\vTools.config.json'             # runtime config
$configSnap = $null
if ((Test-Path $configDst) -and (Test-Path $configSrc)) {
    $configSnap = Get-Content $configDst -Raw -Encoding utf8
}

$dllPath = 'bin\Release\net7.0-windows\vTools.dll'
$dllTimeBefore = if (Test-Path $dllPath) { (Get-Item $dllPath).LastWriteTime } else { $null }

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

# Restore runtime config if MSBuild's PreserveNewest overwrote it
if ($configSnap -ne $null -and (Test-Path $configDst)) {
    $afterContent = Get-Content $configDst -Raw -Encoding utf8
    if ($afterContent -ne $configSnap) {
        Set-Content -Path $configDst -Value $configSnap -NoNewline -Encoding utf8
        Write-Host "Runtime config restored (build overwrote it with project-root copy)." -ForegroundColor Cyan
    }
}

# ── README maintenance ────────────────────────────────────────────────────────
# Helper: write text without BOM (PowerShell Set-Content always adds BOM for utf8).
function Write-Utf8NoBom([string]$path, [string]$text) {
    $enc = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText((Resolve-Path $path), $text, $enc)
}

if (Test-Path $dllPath) {
    $builtVer  = (Get-Item $dllPath).VersionInfo.FileVersion
    $readmePath = 'README.md'
    if ($builtVer -and (Test-Path $readmePath)) {
        $rmContent = [System.IO.File]::ReadAllText((Resolve-Path $readmePath))

        # 1. Update version header.
        $rmUpdated = $rmContent -replace '(?m)^(# vTools\s+\u00b7\s+v)[\d.]+', "`${1}$builtVer"

        # 2. Auto-insert newly added Commands\v*.cs files into README lists.
        #    Detect files added since the last commit (git status --porcelain).
        $newCmds = git diff --cached --name-only --diff-filter=A -- 'Commands/v*.cs' 2>$null
        if (-not $newCmds) {
            # Also check unstaged new files.
            $newCmds = git status --porcelain -- 'Commands/v*.cs' 2>$null |
                       Where-Object { $_ -match '^\?\?' } |
                       ForEach-Object { ($_ -replace '^\?\?\s+','') }
        }
        foreach ($f in $newCmds) {
            $cmdName = [System.IO.Path]::GetFileNameWithoutExtension($f)
            if (-not $cmdName -or -not $cmdName.StartsWith('v')) { continue }
            $anchor = $cmdName.ToLower()
            $entry  = "  - [$cmdName](#$anchor-flow) *($builtVer)* — TODO: add description"
            $link   = "[$cmdName](#$anchor-flow)"

            # Bullet list: insert alphabetically inside the "- Native commands:" block.
            if ($rmUpdated -notmatch [regex]::Escape($link)) {
                $rmUpdated = $rmUpdated -replace '(?m)(^  - \[v[A-Za-z]+\].*\n)(  - \[v[A-Za-z]+\])' , {
                    param($m)
                    $before = $m.Groups[1].Value
                    $next   = $m.Groups[2].Value
                    $prevCmd = ([regex]::Match($before, '\[v([A-Za-z]+)\]')).Groups[1].Value
                    $nextCmd = ([regex]::Match($next,   '\[v([A-Za-z]+)\]')).Groups[1].Value
                    if ([string]::Compare($prevCmd, $cmdName.Substring(1), $true) -lt 0 -and
                        [string]::Compare($cmdName.Substring(1), $nextCmd, $true) -le 0) {
                        "$before$entry`n$next"
                    } else { $m.Value }
                }
                # Inline link list: insert alphabetically.
                $rmUpdated = $rmUpdated -replace "(?<=\[$cmdName.+?\], )\[v" , "[$cmdName](#$anchor-flow), [v" # noop if not matched
                $rmUpdated = $rmUpdated -replace '(\[v[A-Za-z]+\]\(#[^)]+\))(, \[v[A-Za-z]+\]\(#[^)]+\))' , {
                    param($m)
                    $prevCmd = ([regex]::Match($m.Groups[1].Value, '\[v([A-Za-z]+)\]')).Groups[1].Value
                    $nextCmd = ([regex]::Match($m.Groups[2].Value, '\[v([A-Za-z]+)\]')).Groups[1].Value
                    if ([string]::Compare($prevCmd, $cmdName.Substring(1), $true) -lt 0 -and
                        [string]::Compare($cmdName.Substring(1), $nextCmd, $true) -le 0) {
                        "$($m.Groups[1].Value), $link$($m.Groups[2].Value)"
                    } else { $m.Value }
                }
                Write-Host "README: inserted placeholder for $cmdName." -ForegroundColor Cyan
            }
        }

        if ($rmUpdated -ne $rmContent) {
            Write-Utf8NoBom $readmePath $rmUpdated
            if ($rmUpdated -match "v$([regex]::Escape($builtVer))") {
                Write-Host "README updated (v$builtVer)." -ForegroundColor Cyan
            }
        }
    }
}

# Commit only when build succeeded and DLL was actually updated
$dllTimeAfter = if (Test-Path $dllPath) { (Get-Item $dllPath).LastWriteTime } else { $null }
$dllUpdated = ($dllTimeAfter -ne $null) -and ($dllTimeAfter -ne $dllTimeBefore)

if ($dllUpdated) {
    $pendingMsg = (Get-Content $pendingFile -Raw -ErrorAction SilentlyContinue) -replace "`r`n|`r|`n", ' '
    if ($pendingMsg) {
        $ver = (Get-Date).ToString('yy.M.d.HHmm')
        $commitMsg = "${ver}: $pendingMsg"
        git add -A
        git commit -m $commitMsg
        $commitCode = $LASTEXITCODE
        if ($commitCode -ne 0) {
            Write-Host "ERROR: git commit failed (exit $commitCode)" -ForegroundColor Red
        } else {
            Remove-Item $pendingFile -ErrorAction SilentlyContinue
            Write-Host "Committed: $commitMsg" -ForegroundColor Green
            $pushOutput = git push origin main 2>&1
            $pushCode = $LASTEXITCODE
            if ($pushCode -eq 0) {
                Write-Host "Pushed to origin/main." -ForegroundColor Green
            } else {
                Write-Host "WARNING: git push failed (exit $pushCode):" -ForegroundColor Yellow
                Write-Host ($pushOutput | Out-String) -ForegroundColor Yellow
                Write-Host "Commit was created locally. Run 'git push origin main' manually." -ForegroundColor Yellow
            }
        }
    }
} else {
    Write-Host "DLL not updated (build locked or unchanged) - commit deferred." -ForegroundColor Yellow
}
