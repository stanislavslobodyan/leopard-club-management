$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$app = Join-Path $root 'App\LEOPARD.exe'
$seed = Join-Path $root 'Data\club.sqlite3'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'LEOPARD'
$database = Join-Path $dataDirectory 'club.sqlite3'

foreach ($required in @($app, $seed)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "У пакеті немає потрібного файлу: $required"
    }
}

New-Item -ItemType Directory -Force -Path $dataDirectory | Out-Null
if (Test-Path -LiteralPath $database -PathType Leaf) {
    Write-Host "У цьому профілі вже є база LEOPARD: $database"
    $answer = Read-Host 'Замінити її базою з цього набору? Поточну базу буде скопійовано поруч (так/ні)'
    if ($answer -notin @('так', 'т', 'yes', 'y')) {
        Write-Host 'Базу залишено без змін. Запускаю встановлену програму.'
    }
    else {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $backup = "$database.before-transfer-$stamp"
        Copy-Item -LiteralPath $database -Destination $backup
        Copy-Item -LiteralPath $seed -Destination $database -Force
        Write-Host "Стару базу збережено: $backup"
    }
}
else {
    Copy-Item -LiteralPath $seed -Destination $database
    Write-Host "Базу встановлено: $database"
}

Start-Process -FilePath $app -WorkingDirectory (Split-Path -Parent $app)
