param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$OutFile,
    [string]$Changelog = "CHANGELOG.md"
)
# Writes one version's CHANGELOG.md section (without its heading) to a UTF-8 file for the release notes.
$text = [System.IO.File]::ReadAllText((Resolve-Path $Changelog))
$pattern = "(?ms)^## \[" + [regex]::Escape($Version) + "\][^\n]*\n(.*?)(?=^## \[|^\[[^\]]+\]: )"
$match = [regex]::Match($text, $pattern)
if (-not $match.Success -or -not $match.Groups[1].Value.Trim()) { throw "CHANGELOG.md has no entry for $Version. Add one before tagging." }
$notes = $match.Groups[1].Value.Trim() + "`n`n---`nInstall: download ``OpenPointer-Setup-$Version.exe`` below. Verify downloads with ``SHA256SUMS.txt``.`n"
[System.IO.File]::WriteAllText($OutFile, $notes, (New-Object System.Text.UTF8Encoding $false))
