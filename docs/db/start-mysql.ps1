# Starts the portable MySQL used for local development (C:\mysql-portable, see docs/db/README.md).
# It runs as a hidden background process listening on 127.0.0.1:3306 until stop-mysql.ps1 or a reboot.
param([string]$Root = 'C:\mysql-portable')

$mysqld = Join-Path $Root 'server\bin\mysqld.exe'
$config = Join-Path $Root 'my.ini'
if (-not (Test-Path $mysqld)) { throw "MySQL is not installed in $Root (see docs/db/README.md)." }

if (Get-NetTCPConnection -LocalPort 3306 -State Listen -ErrorAction SilentlyContinue) {
    Write-Host 'MySQL is already running on 127.0.0.1:3306.'
    return
}

Start-Process -FilePath $mysqld -ArgumentList "--defaults-file=$config" -WindowStyle Hidden
for ($i = 0; $i -lt 30; $i++) {
    if (Get-NetTCPConnection -LocalPort 3306 -State Listen -ErrorAction SilentlyContinue) {
        Write-Host 'MySQL started on 127.0.0.1:3306.'
        return
    }
    Start-Sleep -Seconds 1
}
throw "MySQL did not start within 30 s; see $(Join-Path $Root 'mysqld.err')."
