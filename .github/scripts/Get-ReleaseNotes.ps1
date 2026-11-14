<#
.SYNOPSIS
  Cuts the section of one version out of CHANGELOG.md (the lines below "## <Version>" up to the next "## " heading).

.DESCRIPTION
  Used by .github/workflows/release.yml for the text of the GitHub release. Fails (exit code 1) if the section is
  missing or empty, so that a release is never published without notes. Runs in Windows PowerShell 5.1 and PowerShell 7.
  The heading must be exactly "## <Version>", optionally followed by a space and more text (e.g. a date).

.EXAMPLE
  .github/scripts/Get-ReleaseNotes.ps1 -ChangelogPath CHANGELOG.md -Version 0.1.0 -OutputPath release-notes.md
#>
param(
  [Parameter(Mandatory = $true)][string]$ChangelogPath,
  [Parameter(Mandatory = $true)][string]$Version,
  [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$Version = $Version.TrimStart('v', 'V')
$utf8 = New-Object System.Text.UTF8Encoding($false)
$lines = [System.IO.File]::ReadAllLines((Resolve-Path -LiteralPath $ChangelogPath).Path, $utf8)

$heading = '^##\s+' + [regex]::Escape($Version) + '(\s.*)?$'
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -match $heading) { $start = $i; break }
}
if ($start -lt 0) {
  Write-Error "CHANGELOG.md has no section '## $Version'."
  exit 1
}

$notes = New-Object System.Collections.Generic.List[string]
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -match '^##\s') { break }
  $notes.Add($lines[$i])
}

$text = ($notes -join "`n").Trim()
if ($text.Length -eq 0) {
  Write-Error "The section '## $Version' in CHANGELOG.md is empty."
  exit 1
}

$full = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path (Get-Location).Path $OutputPath }
[System.IO.File]::WriteAllText($full, $text + "`n", $utf8)
