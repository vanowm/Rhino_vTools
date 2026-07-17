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
        $newCmds = git diff --cached --name-only --diff-filter=A -- 'Commands/v*.cs' 2>$null
        if (-not $newCmds) {
            $newCmds = (git status --porcelain -- 'Commands/v*.cs' 2>$null) |
                       Where-Object { $_ -match '^\?\?' } |
                       ForEach-Object { $_ -replace '^\?\?\s+','' }
        }
        foreach ($f in $newCmds) {
            $cmdName = [System.IO.Path]::GetFileNameWithoutExtension($f)
            if (-not $cmdName -or -not $cmdName.StartsWith('v')) { continue }
            $anchor  = $cmdName.ToLower()
            $link    = "[$cmdName](#$anchor-flow)"
            if ($rmUpdated -notmatch [regex]::Escape($link)) {
                # Insert into bullet list alphabetically.
                $desc  = 'TODO: add description'
                $entry = "  - $link *($builtVer)* `u2014 $desc"
                # Find insertion point: first line where the next command sorts after cmdName.
                $lines = $rmUpdated -split "`n"
                $inserted = $false
                for ($li = 0; $li -lt $lines.Count - 1; $li++) {
                    if ($lines[$li] -match '^  - \[v([A-Za-z]+)\]' -and
                        $lines[$li+1] -match '^  - \[v([A-Za-z]+)\]') {
                        $prev = $matches[1]
                        $null = $lines[$li+1] -match '^  - \[v([A-Za-z]+)\]'; $next = $matches[1]
                        if ([string]::Compare($prev,$cmdName.Substring(1),$true) -lt 0 -and
                            [string]::Compare($cmdName.Substring(1),$next,$true) -le 0) {
                            $lines = $lines[0..$li] + $entry + $lines[($li+1)..($lines.Count-1)]
                            $inserted = $true
                            break
                        }
                    }
                }
                if ($inserted) {
                    $rmUpdated = $lines -join "`n"
                    # Insert into flat inline list alphabetically.
                    $rmUpdated = $rmUpdated -replace "(\[$([regex]::Escape($cmdName))\][^,]+, )\[v" , "`$1[v"  # noop guard
                    $rmUpdated = $rmUpdated -replace `
                        "(\[v[A-Za-z]+\]\(#[^)]+\))(, \[v([A-Za-z]+)\]\(#[^)]+\))" , {
                        param($m)
                        $p = ([regex]::Match($m.Groups[1].Value,'\[v([A-Za-z]+)\]')).Groups[1].Value
                        $nx = $m.Groups[3].Value
                        if ([string]::Compare($p,$cmdName.Substring(1),$true) -lt 0 -and
                            [string]::Compare($cmdName.Substring(1),$nx,$true) -le 0) {
                            "$($m.Groups[1].Value), $link$($m.Groups[2].Value)"
                        } else { $m.Value }
                    }
                    Write-Host "README: inserted placeholder for $cmdName." -ForegroundColor Cyan
                }
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
