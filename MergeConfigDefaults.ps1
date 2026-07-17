param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$DestinationPath
)

$ErrorActionPreference = 'Stop'

function Read-JsonObject([string]$Path) {
    $text = [System.IO.File]::ReadAllText($Path)
    return $text | ConvertFrom-Json
}

function Merge-MissingProperties($Defaults, $Current) {
    $changed = $false

    foreach ($property in $Defaults.PSObject.Properties) {
        $existing = $Current.PSObject.Properties[$property.Name]
        if ($null -eq $existing) {
            $Current | Add-Member -MemberType NoteProperty -Name $property.Name -Value $property.Value
            $changed = $true
            continue
        }

        $defaultValue = $property.Value
        $currentValue = $existing.Value
        if ($defaultValue -is [pscustomobject] -and $currentValue -is [pscustomobject]) {
            if (Merge-MissingProperties $defaultValue $currentValue) {
                $changed = $true
            }
        }
    }

    return $changed
}

if (-not (Test-Path -LiteralPath $SourcePath)) {
    throw "Default config not found: $SourcePath"
}

$destinationDirectory = Split-Path -Parent $DestinationPath
if (-not [string]::IsNullOrWhiteSpace($destinationDirectory)) {
    [System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
}

if (-not (Test-Path -LiteralPath $DestinationPath)) {
    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath
    Write-Host "Runtime config created from defaults: $DestinationPath"
    exit 0
}

$defaults = Read-JsonObject $SourcePath
$current = Read-JsonObject $DestinationPath
if (Merge-MissingProperties $defaults $current) {
    $json = $current | ConvertTo-Json -Depth 100
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($DestinationPath, $json, $encoding)
    Write-Host "Runtime config updated with missing defaults: $DestinationPath"
}
