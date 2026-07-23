param(
  [string]$IconDirectory = (Join-Path $PSScriptRoot "icons"),
  [string]$OutputPath = (Join-Path $PSScriptRoot "vTools.rui")
)

$ErrorActionPreference = "Stop"

$pluginId = "2607512e-a1fc-4cf9-9329-a293431437a0"
$mainToolbarId = "f00df249-4c86-4080-9c11-3360fdf269ef"
$isolateToolbarId = "a8edc5d5-146e-4595-b3ae-4dc971efb239"
$isolateFlyoutButtonId = "e07b6c70-b773-48dc-af30-8f7047165b96"
# Keep toolbar and button IDs stable to preserve user layout. Rotate only an
# affected LeftMacroId or ShowId when its script changes so Rhino reloads it.
$buttons = @(
  @{
    Name = "vIsolate"
    RightName = "vShow"
    Tooltip = "Isolate objects"
    RightTooltip = "Show objects"
    Set = $null
    Id = "1119e14d-9a8e-40bc-a985-c2aacf2435d6"
    LeftMacroId = "1119e14d-9a8e-40bc-a985-c2aacf2435d6"
    ShowId = "87976eef-39e5-4f85-856f-69f74a5021cb"
    Icon = "vIsolate.svg"
  },
  @{
    Name = "vIsolate A"
    Set = "A"
    Id = "43aef707-2579-4959-9eea-a790dfb1a157"
    LeftMacroId = "da8cb468-f80d-48b9-b6f0-a346f12f9016"
    ShowId = "93d12f9e-b57b-41cc-a335-412e5348136b"
    Icon = "vIsolate_A.svg"
  },
  @{
    Name = "vIsolate B"
    Set = "B"
    Id = "b5c58f34-5a56-49fa-8d83-c57ba9c5e0cb"
    LeftMacroId = "ff335263-1574-4707-9ea5-762bf2ad18c2"
    ShowId = "b415a8de-d031-406d-8590-b44a23a4f654"
    Icon = "vIsolate_B.svg"
  },
  @{
    Name = "vIsolate C"
    Set = "C"
    Id = "3991bed5-68c9-4858-ad7d-dd6db0c3dd90"
    LeftMacroId = "74d18e5f-d7e6-47a1-8e5c-5076cbb262db"
    ShowId = "b26b871a-cd89-4cff-be93-343de0063a2a"
    Icon = "vIsolate_C.svg"
  },
  @{
    Name = "vIsolate D"
    Set = "D"
    Id = "3d3669d6-a07d-413b-ac7b-59ffc3e2490c"
    LeftMacroId = "66b5f703-a129-41b0-970e-592af28d6889"
    ShowId = "58a85580-d367-4e52-9ed1-5f233c072ee6"
    Icon = "vIsolate_D.svg"
  },
  @{
    Name = "vIsolate E"
    Set = "E"
    Id = "7e0dae8b-91a1-48b8-aaf3-ba3fa26069ad"
    LeftMacroId = "e757c304-347e-4b92-8bb8-481816cb83a6"
    ShowId = "481d3c31-714c-4b25-a465-36c10a976761"
    Icon = "vIsolate_E.svg"
  },
  @{
    Name = "Isolate"
    RightName = "Show"
    Tooltip = "Isolate objects"
    RightTooltip = "Show objects"
    Id = "b9b9a3ff-9b75-4011-9b6f-1afc098154d8"
    LeftMacroId = "935bdc76-5ed9-44c4-8286-2ee3774f45ce"
    ShowId = "914df64f-c714-49fc-a38c-baa4598c49eb"
    LeftScript = '!_Isolate'
    RightScript = '!_Show'
    Icon = "Isolate_Show.svg"
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
  $writer.WriteAttributeString("guid", $mainToolbarId)
  $writer.WriteAttributeString("dock_bar_guid32", $mainToolbarId)
  $writer.WriteAttributeString("dock_bar_guid64", $mainToolbarId)
  $writer.WriteAttributeString("active_tool_bar_group", $mainToolbarId)
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
  $writer.WriteAttributeString("guid", $mainToolbarId)
  $writer.WriteAttributeString("major_version", "1")
  $writer.WriteAttributeString("minor_version", "1")
  $writer.WriteStartElement("text")
  $writer.WriteElementString("locale_1033", "vTools")
  $writer.WriteEndElement()
  $writer.WriteElementString("tool_bar_id", $mainToolbarId)
  $writer.WriteEndElement()
  $writer.WriteEndElement()
  $writer.WriteEndElement()

  $writer.WriteStartElement("tool_bars")
  $writer.WriteStartElement("tool_bar")
  $writer.WriteAttributeString("guid", $mainToolbarId)
  $writer.WriteAttributeString("bitmap_id", $buttons[0].Id)
  $writer.WriteAttributeString("item_display_style", "control_only")
  $writer.WriteStartElement("text")
  $writer.WriteElementString("locale_1033", "vTools")
  $writer.WriteEndElement()
  $writer.WriteStartElement("tool_bar_item")
  $writer.WriteAttributeString("guid", $isolateFlyoutButtonId)
  $writer.WriteAttributeString("button_display_mode", "control_only")
  $writer.WriteAttributeString("button_style", "normal")
  $writer.WriteStartElement("text")
  $writer.WriteElementString("locale_1033", "vIsolate / vShow")
  $writer.WriteEndElement()
  $writer.WriteElementString("left_macro_id", $buttons[0].LeftMacroId)
  $writer.WriteElementString("right_macro_id", $buttons[0].ShowId)
  $writer.WriteStartElement("link")
  $writer.WriteAttributeString("style", "normal")
  $writer.WriteString($isolateToolbarId)
  $writer.WriteEndElement()
  $writer.WriteEndElement()
  $writer.WriteEndElement()

  $writer.WriteStartElement("tool_bar")
  $writer.WriteAttributeString("guid", $isolateToolbarId)
  $writer.WriteAttributeString("bitmap_id", $buttons[0].Id)
  $writer.WriteAttributeString("item_display_style", "control_only")
  $writer.WriteStartElement("text")
  $writer.WriteElementString("locale_1033", "vIsolate")
  $writer.WriteEndElement()
  foreach ($button in $buttons) {
    $writer.WriteStartElement("tool_bar_item")
    $writer.WriteAttributeString("guid", $button.Id)
    $writer.WriteAttributeString("button_display_mode", "control_only")
    $writer.WriteAttributeString("button_style", "normal")
    $writer.WriteStartElement("text")
    $writer.WriteElementString("locale_1033", $button.Name)
    $writer.WriteEndElement()
    $writer.WriteElementString("left_macro_id", $button.LeftMacroId)
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
    $writer.WriteAttributeString("guid", $button.LeftMacroId)
    $writer.WriteAttributeString("bitmap_id", $button.Id)
    foreach ($element in @("text", "tooltip", "help_text", "button_text", "menu_text")) {
      $writer.WriteStartElement($element)
      $label = if ($element -eq "tooltip" -and $button.Tooltip) {
        $button.Tooltip
      }
      else {
        $button.Name
      }
      $writer.WriteElementString("locale_1033", $label)
      $writer.WriteEndElement()
    }
    $leftScript = if ($button.LeftScript) {
      $button.LeftScript
    }
    elseif ($button.Set) {
      "'_vIsolate `"$($button.Set)`""
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
      $showName = if ($button.RightName) {
        $button.RightName
      }
      elseif ($button.Set) {
        "vShow $($button.Set)"
      }
      else {
        "vShow"
      }
      foreach ($element in @("text", "tooltip", "help_text", "button_text", "menu_text")) {
        $writer.WriteStartElement($element)
        $label = if ($element -eq "tooltip" -and $button.RightTooltip) {
          $button.RightTooltip
        }
        else {
          $showName
        }
        $writer.WriteElementString("locale_1033", $label)
        $writer.WriteEndElement()
      }
      $showScript = if ($button.RightScript) {
        $button.RightScript
      }
      elseif ($button.Set) {
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
