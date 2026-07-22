param(
  [string]$IconDirectory = (Join-Path $PSScriptRoot "icons"),
  [string]$OutputPath = (Join-Path $PSScriptRoot "vTools.rui")
)

$ErrorActionPreference = "Stop"

$pluginId = "2607512e-a1fc-4cf9-9329-a293431437a0"
$toolbarId = "f00df249-4c86-4080-9c11-3360fdf269ef"
$buttons = @(
  @{
    Name = "vIsolate"
    Set = $null
    Id = "1119e14d-9a8e-40bc-a985-c2aacf2435d6"
    ShowId = "fec0ae7a-3fbd-4402-b439-003ab9e96ebc"
    Icon = "vIsolate.svg"
  },
  @{
    Name = "vIsolate A"
    Set = "A"
    Id = "43aef707-2579-4959-9eea-a790dfb1a157"
    ShowId = "6dac69f7-798c-4280-8118-3578595b69f5"
    Icon = "vIsolate_A.svg"
  },
  @{
    Name = "vIsolate B"
    Set = "B"
    Id = "b5c58f34-5a56-49fa-8d83-c57ba9c5e0cb"
    ShowId = "44397787-82f4-42ac-ac6e-6c2d13485759"
    Icon = "vIsolate_B.svg"
  },
  @{
    Name = "vIsolate C"
    Set = "C"
    Id = "3991bed5-68c9-4858-ad7d-dd6db0c3dd90"
    ShowId = "7349c359-ffdf-400a-a79e-21ba69ec2071"
    Icon = "vIsolate_C.svg"
  },
  @{
    Name = "vIsolate D"
    Set = "D"
    Id = "3d3669d6-a07d-413b-ac7b-59ffc3e2490c"
    ShowId = "8ac0f5df-8ec8-438e-8c62-8185246c5a1d"
    Icon = "vIsolate_D.svg"
  },
  @{
    Name = "vIsolate E"
    Set = "E"
    Id = "7e0dae8b-91a1-48b8-aaf3-ba3fa26069ad"
    ShowId = "4af6fb26-2cb0-408c-9be2-80f4b1a775fe"
    Icon = "vIsolate_E.svg"
  }
)

function Get-SvgXml([string]$path) {
  if (-not (Test-Path -LiteralPath $path)) {
    throw "Toolbar icon not found: $path"
  }

  $svg = New-Object System.Xml.XmlDocument
  $svg.PreserveWhitespace = $true
  $svg.XmlResolver = $null
  $svg.Load($path)
  return $svg.DocumentElement.OuterXml
}

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.IndentChars = "  "
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)
$settings.NewLineChars = "`r`n"

$directory = Split-Path -Parent $OutputPath
if ($directory) {
  [System.IO.Directory]::CreateDirectory($directory) | Out-Null
}

$writer = [System.Xml.XmlWriter]::Create($OutputPath, $settings)
try {
  $writer.WriteStartDocument()
  $writer.WriteStartElement("RhinoUI")
  $writer.WriteAttributeString("guid", $pluginId)
  $writer.WriteAttributeString("plug_in_guid", $pluginId)
  $writer.WriteAttributeString("major_ver", "8")
  $writer.WriteAttributeString("minor_ver", "0")
  $writer.WriteAttributeString("localize", "False")
  $writer.WriteAttributeString("default_language_id", "1033")

  $writer.WriteStartElement("extend_rhino_menus")
  $writer.WriteEndElement()
  $writer.WriteStartElement("menus")
  $writer.WriteEndElement()

  $writer.WriteStartElement("tool_bar_groups")
  $writer.WriteStartElement("tool_bar_group")
  $writer.WriteAttributeString("guid", $toolbarId)
  $writer.WriteAttributeString("dock_bar_guid32", $toolbarId)
  $writer.WriteAttributeString("dock_bar_guid64", $toolbarId)
  $writer.WriteAttributeString("active_tool_bar_group", $toolbarId)
  $writer.WriteAttributeString("single_file", "False")
  $writer.WriteAttributeString("hide_single_tab", "True")
  $writer.WriteStartElement("text")
  $writer.WriteElementString("locale_1033", "vTools")
  $writer.WriteEndElement()
  $writer.WriteStartElement("dock_bar_info")
  $writer.WriteAttributeString("visible", "True")
  $writer.WriteAttributeString("floating", "True")
  $writer.WriteEndElement()
  $writer.WriteStartElement("tool_bar_group_item")
  $writer.WriteAttributeString("guid", $toolbarId)
  $writer.WriteAttributeString("major_version", "1")
  $writer.WriteAttributeString("minor_version", "1")
  $writer.WriteStartElement("text")
  $writer.WriteElementString("locale_1033", "vTools")
  $writer.WriteEndElement()
  $writer.WriteElementString("tool_bar_id", $toolbarId)
  $writer.WriteEndElement()
  $writer.WriteEndElement()
  $writer.WriteEndElement()

  $writer.WriteStartElement("tool_bars")
  $writer.WriteStartElement("tool_bar")
  $writer.WriteAttributeString("guid", $toolbarId)
  $writer.WriteAttributeString("bitmap_id", $buttons[0].Id)
  $writer.WriteAttributeString("item_display_style", "control_only")
  $writer.WriteStartElement("text")
  $writer.WriteElementString("locale_1033", "vTools")
  $writer.WriteEndElement()
  foreach ($button in $buttons) {
    $writer.WriteStartElement("tool_bar_item")
    $writer.WriteAttributeString("guid", $button.Id)
    $writer.WriteAttributeString("button_display_mode", "control_only")
    $writer.WriteAttributeString("button_style", "normal")
    $writer.WriteStartElement("text")
    $writer.WriteElementString("locale_1033", $button.Name)
    $writer.WriteEndElement()
    $writer.WriteElementString("left_macro_id", $button.Id)
    if ($button.ShowId) {
      $writer.WriteElementString("right_macro_id", $button.ShowId)
    }
    $writer.WriteEndElement()
  }
  $writer.WriteEndElement()
  $writer.WriteEndElement()

  $writer.WriteStartElement("macros")
  foreach ($button in $buttons) {
    $writer.WriteStartElement("macro_item")
    $writer.WriteAttributeString("guid", $button.Id)
    $writer.WriteAttributeString("bitmap_id", $button.Id)
    foreach ($element in @("text", "tooltip", "help_text", "button_text", "menu_text")) {
      $writer.WriteStartElement($element)
      $writer.WriteElementString("locale_1033", $button.Name)
      $writer.WriteEndElement()
    }
    $leftScript = if ($button.Set) {
      "'_vIsolate _$($button.Set)"
    }
    else {
      "'_vIsolate"
    }
    $writer.WriteElementString("script", $leftScript)
    $writer.WriteEndElement()

    if ($button.ShowId) {
      $writer.WriteStartElement("macro_item")
      $writer.WriteAttributeString("guid", $button.ShowId)
      $writer.WriteAttributeString("bitmap_id", $button.Id)
      $showName = if ($button.Set) { "vShow $($button.Set)" } else { "vShow" }
      foreach ($element in @("text", "tooltip", "help_text", "button_text", "menu_text")) {
        $writer.WriteStartElement($element)
        $writer.WriteElementString("locale_1033", $showName)
        $writer.WriteEndElement()
      }
      $showScript = if ($button.Set) {
        "'_vShow `"$($button.Set)`""
      }
      else {
        "'_vShow"
      }
      $writer.WriteElementString("script", $showScript)
      $writer.WriteEndElement()
    }
  }
  $writer.WriteEndElement()

  $writer.WriteStartElement("icons")
  foreach ($button in $buttons) {
    $svg = Get-SvgXml (Join-Path $IconDirectory $button.Icon)
    $writer.WriteStartElement("icon")
    $writer.WriteAttributeString("guid", $button.Id)
    $writer.WriteStartElement("light")
    $writer.WriteRaw($svg)
    $writer.WriteEndElement()
    $writer.WriteStartElement("dark")
    $writer.WriteRaw($svg)
    $writer.WriteEndElement()
    $writer.WriteEndElement()
  }
  $writer.WriteEndElement()

  $writer.WriteEndElement()
  $writer.WriteEndDocument()
}
finally {
  $writer.Dispose()
}

Write-Host "Generated $OutputPath"
