# Run: powershell -ExecutionPolicy Bypass -File tools\build-manual-pdf.ps1
# Converts docs\USER_MANUAL.html to docs\USER_MANUAL.pdf with headless Edge/Chrome.
$root = Split-Path -Parent $PSScriptRoot
$html = Join-Path $root 'docs\USER_MANUAL.html'
$pdf = Join-Path $root 'docs\USER_MANUAL.pdf'
$browser = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw 'Edge or Chrome is required.' }
$uri = ([System.Uri]$html).AbsoluteUri
Start-Process -Wait -FilePath $browser -ArgumentList @('--headless', '--disable-gpu', '--no-pdf-header-footer', "--print-to-pdf=`"$pdf`"", $uri)
Get-Item $pdf | Select-Object FullName, Length
