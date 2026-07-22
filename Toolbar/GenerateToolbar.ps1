param(
  [string]$IconDirectory = (Join-Path $PSScriptRoot "..\..\icons"),
  [string]$OutputPath = (Join-Path $PSScriptRoot "..\vTools.rui")
)

$ErrorActionPreference = "Stop"

$pluginId = "2607512e-a1fc-4cf9-9329-a293431437a0"
$toolbarId = "f00df249-4c86-4080-9c11-3360fdf269ef"
$buttons = @(
  @{
    Name = "Isolate A"
    Set = "A"
    Id = "43aef707-2579-4959-9eea-a790dfb1a157"
    ShowId = "6dac69f7-798c-4280-8118-3578595b69f5"
    Icon = "Isolate_A.svg"
  },
  @{
    Name = "Isolate B"
    Set = "B"
    Id = "b5c58f34-5a56-49fa-8d83-c57ba9c5e0cb"
    ShowId = "44397787-82f4-42ac-ac6e-6c2d13485759"
    Icon = "Isolate_B.svg"
  },
  @{
    Name = "Isolate C"
    Set = "C"
    Id = "3991bed5-68c9-4858-ad7d-dd6db0c3dd90"
    ShowId = "7349c359-ffdf-400a-a79e-21ba69ec2071"
    Icon = "Isolate_C.svg"
  }
)

function Get-SvgXml([string]$path) {
  if (-not (Test-Path -LiteralPath $path)) {
    throw "Toolbar icon not found: $path"
  }

  $svg = New-Object System.Xml.XmlDocument
  $svg.PreserveWhitespace = $true
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
  $writer.WriteElementString("locale_1033", "vTools Isolate")
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
  $writer.WriteElementString("locale_1033", "vTools Isolate")
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
  $writer.WriteElementString("locale_1033", "vTools Isolate")
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
    $writer.WriteElementString("right_macro_id", $button.ShowId)
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
    $writer.WriteElementString("script", "'_vIsolate _$($button.Set)")
    $writer.WriteEndElement()

    $writer.WriteStartElement("macro_item")
    $writer.WriteAttributeString("guid", $button.ShowId)
    $writer.WriteAttributeString("bitmap_id", $button.Id)
    foreach ($element in @("text", "tooltip", "help_text", "button_text", "menu_text")) {
      $writer.WriteStartElement($element)
      $writer.WriteElementString("locale_1033", "Show $($button.Set)")
      $writer.WriteEndElement()
    }
    $writer.WriteElementString("script", "'_-Show `"$($button.Set)`"")
    $writer.WriteEndElement()
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
