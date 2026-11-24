# Veroeffentlicht die Version aus src\App.cs (Program.Version) als GitHub-Release.
# Ablauf: Version in App.cs + Abschnitt in CHANGELOG.md -> committen + pushen -> dieses Skript.
# Die installierten Programme holen sich das Release per Auto-Update.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = 'epm-enline/ip-umschalter'

$ver = [regex]::Match([IO.File]::ReadAllText("$root\src\App.cs"), 'const string Version = "([\d.]+)"').Groups[1].Value
if (-not $ver) { throw 'Version in src\App.cs nicht gefunden' }

# Changelog-Abschnitt der Version = Release-Text
$cl = [IO.File]::ReadAllText("$root\CHANGELOG.md")
$m = [regex]::Match($cl, "(?ms)^## \[$([regex]::Escape($ver))\][^\r\n]*\r?\n(.*?)(?=^## \[|\z)")
if (-not $m.Success) { throw "CHANGELOG.md hat keinen Abschnitt ## [$ver]" }
$notes = $m.Groups[1].Value.Trim()

if (git -C $root status --porcelain) { throw 'Arbeitsbaum nicht sauber - erst committen' }
git -C $root fetch -q origin
if ((git -C $root rev-parse HEAD) -ne (git -C $root rev-parse origin/main)) { throw 'HEAD ist nicht auf origin/main - erst pushen' }
gh release view "v$ver" --repo $repo *> $null
if ($LASTEXITCODE -eq 0) { throw "Release v$ver gibt es schon - Version erhoehen" }

& "$root\build.ps1"
$st = Join-Path $env:TEMP 'ipumschalter-release-selftest.txt'
$p = Start-Process "$root\IP-Umschalter.exe" -ArgumentList '--selftest', $st -Wait -PassThru
if ($p.ExitCode -ne 0) { Get-Content $st | Select-String '^FAIL'; throw 'Selbsttest rot' }
(Get-Content $st -Encoding UTF8)[-1]

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item "$root\IP-Umschalter.exe" "$dist\IP-Umschalter.exe" -Force
$hash = (Get-FileHash "$dist\IP-Umschalter.exe" -Algorithm SHA256).Hash.ToLower()
[IO.File]::WriteAllText("$dist\IP-Umschalter.exe.sha256", "$hash  IP-Umschalter.exe`n")

$nf = Join-Path $dist 'notes.md'
[IO.File]::WriteAllText($nf, $notes, (New-Object Text.UTF8Encoding $false))
gh release create "v$ver" "$dist\IP-Umschalter.exe" "$dist\IP-Umschalter.exe.sha256" --repo $repo --target main --title "v$ver" --notes-file $nf
if ($LASTEXITCODE -ne 0) { throw 'gh release create fehlgeschlagen' }
"Veroeffentlicht: v$ver ($hash)"
