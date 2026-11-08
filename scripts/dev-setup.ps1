# Prepara el entorno local: genera valores al azar en .env (fuera del repo) y los carga en dotnet user-secrets.
# Uso: .\scripts\dev-setup.ps1 [-DbPassword <clave>]
#   -DbPassword: solo si ya existe el volumen de Postgres y fue creado con otra clave.
#   Si .env ya existe, se reutiliza.
param([string]$DbPassword)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$envFile = Join-Path $root '.env'

function New-RandomHex([int]$bytes) {
    $buffer = New-Object byte[] $bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($buffer)
    $rng.Dispose()
    return -join ($buffer | ForEach-Object { $_.ToString('x2') })
}

if (-not (Test-Path $envFile)) {
    if (-not $DbPassword) { $DbPassword = New-RandomHex 16 }
    $lines = @(
        "RAF_DB_PASSWORD=$DbPassword",
        "RAF_JWT_SIGNING_KEY=$(New-RandomHex 32)",
        "RAF_ADMIN_PASSWORD=Aa1$(New-RandomHex 12)"
    )
    [System.IO.File]::WriteAllLines($envFile, $lines)
    Write-Output 'Generado .env'
} else {
    Write-Output 'Usando el .env existente'
}

$values = @{}
Get-Content $envFile | ForEach-Object {
    if ($_ -match '^\s*([A-Z_]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] }
}

$project = Join-Path $root 'src/Raf.Server'
$connection = "Host=localhost;Port=5433;Database=raf;Username=raf;Password=$($values['RAF_DB_PASSWORD'])"
dotnet user-secrets set --project $project 'ConnectionStrings:Raf' $connection | Out-Null
dotnet user-secrets set --project $project 'Jwt:SigningKey' $values['RAF_JWT_SIGNING_KEY'] | Out-Null
dotnet user-secrets set --project $project 'Bootstrap:AdminPassword' $values['RAF_ADMIN_PASSWORD'] | Out-Null

Write-Output 'Listo. La clave del admin local es RAF_ADMIN_PASSWORD en .env'
