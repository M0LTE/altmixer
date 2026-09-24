<#
  Writes release notes for a version.
  Uses the "## <version>" section of CHANGELOG.md if there is one; otherwise lists the commits since the previous tag.
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Ref,
    [Parameter(Mandatory)] [string] $OutFile
)
$ErrorActionPreference = 'Stop'
$repo = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'M0LTE/altmixer' }
# Nearest earlier version tag, if any.
$previous = git describe --tags --abbrev=0 --match '[0-9]*.[0-9]*.[0-9]*' --match 'v[0-9]*.[0-9]*.[0-9]*' "$Ref^" 2>$null
if ($LASTEXITCODE -ne 0) { $previous = $null }
$global:LASTEXITCODE = 0

$notes = New-Object System.Collections.Generic.List[string]

$changelog = if (Test-Path CHANGELOG.md) { Get-Content CHANGELOG.md } else { @() }
$start = [Array]::FindIndex([string[]]$changelog, [Predicate[string]] { param($l) $l -match "^##\s+\[?v?$([regex]::Escape($Version))\]?(\s|$)" })
if ($start -ge 0) {
    $section = $changelog[($start + 1)..($changelog.Count - 1)]
    $end = [Array]::FindIndex([string[]]$section, [Predicate[string]] { param($l) $l -match '^##\s' })
    if ($end -ge 0) { $section = $section[0..($end - 1)] }
    $notes.AddRange([string[]]($section | ForEach-Object { $_ }))
} else {
    Write-Warning "No '## $Version' section in CHANGELOG.md; listing commits instead."
    $range = if ($previous) { "$previous..$Ref" } else { $Ref }
    $notes.Add('## Changes')
    $notes.Add('')
    git log --no-merges --pretty=format:'- %s' $range | ForEach-Object { $notes.Add($_) }
}

$notes.Add('')
$notes.Add('## Downloads')
$notes.Add('')
$notes.Add("- **AltMixer-$Version-win-x64.msi**: installs to Program Files with a Start menu shortcut; later versions upgrade in place.")
$notes.Add("- **AltMixer-$Version-win-x64.exe**: the same app as a single portable file.")
$notes.Add('')
$notes.Add('Requires Windows 11 x64 and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64). If it is missing, Windows offers the download when AltMixer starts.')
$notes.Add('')
$notes.Add($(if ($previous) { "**Full changelog**: https://github.com/$repo/compare/$previous...$Ref" } else { "**Commits**: https://github.com/$repo/commits/$Ref" }))

# Trim leading/trailing blank lines.
$text = ($notes -join "`n").Trim() + "`n"
Set-Content -Path $OutFile -Value $text -NoNewline -Encoding utf8
Write-Host $text
