# Stops the portable MySQL cleanly (C:\mysql-portable, see docs/db/README.md).
param([string]$Root = 'C:\mysql-portable')

if (-not (Get-NetTCPConnection -LocalPort 3306 -State Listen -ErrorAction SilentlyContinue)) {
    Write-Host 'MySQL is not running.'
    return
}

$password = Get-Content (Join-Path $Root 'root-password.txt') -Raw
# The password goes through an environment variable, not the command line.
$env:MYSQL_PWD = $password.Trim()
try {
    & (Join-Path $Root 'server\bin\mysqladmin.exe') "--defaults-file=$(Join-Path $Root 'my.ini')" -u root shutdown
}
finally {
    Remove-Item Env:\MYSQL_PWD -ErrorAction SilentlyContinue
}
Write-Host 'MySQL stopped.'
