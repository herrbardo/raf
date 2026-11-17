# Despliega RAF en Railway: aplica la infraestructura de .railway/railway.ts, genera los secretos que falten y sube
# el código del servidor y del cliente web. Railway construye cada imagen y corre las migraciones (preDeploy) antes
# de pasar el tráfico a la versión nueva. No guarda credenciales: usa la sesión de la CLI (`railway login`) y el
# proyecto vinculado.
#
# Uso (desde la raíz del repo; ver infra/railway/README.md):
#   railway link                                              # una vez: elegir proyecto y entorno
#   .\infra\railway\deploy.ps1 -AdminEmail <tu-email>          # primer despliegue
#   .\infra\railway\deploy.ps1                                 # versiones siguientes
#   .\infra\railway\deploy.ps1 -Only web                       # solo el cliente web (o -Only server)
param(
    [string]$AdminEmail,
    [string]$Service = 'server',
    [string]$WebService = 'web',
    [ValidateSet('all', 'server', 'web')]
    [string]$Only = 'all',
    [switch]$Yes # aplica la infraestructura sin pedir confirmación
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Push-Location $root
try {
    railway whoami *> $null
    if ($LASTEXITCODE -ne 0) { throw 'La CLI de Railway no tiene sesión: correr `railway login`.' }
    railway status *> $null
    if ($LASTEXITCODE -ne 0) { throw 'El repo no está vinculado a un proyecto de Railway: correr `railway link`.' }

    # El SDK que evalúa railway.ts (en .railway/node_modules) controla la versión de la CLI ejecutando "railway"
    # sin shell; en Windows eso no encuentra el railway.cmd de npm y falla aunque la versión alcance. Acepta la ruta
    # del ejecutable en la variable "_".
    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        $exe = Join-Path (Split-Path (Get-Command railway).Source) 'node_modules\@railway\cli\bin\railway.exe'
        if (Test-Path $exe) { $env:_ = $exe }
    }
    if (-not (Test-Path (Join-Path $root '.railway/node_modules/railway'))) {
        npm install --prefix (Join-Path $root '.railway') --ignore-scripts
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo instalar el SDK de Railway en .railway.' }
    }

    # 1. Infraestructura: base, servicio y variables que no son secretas.
    $applyArgs = @('config', 'apply')
    if ($Yes) { $applyArgs += '--yes' }
    railway @applyArgs
    if ($LASTEXITCODE -ne 0) { throw 'Falló railway config apply.' }

    # 2. Secretos: solo se generan si no existen. Una variable sellada figura sin valor, pero existe.
    $json = railway variable list --service $Service --json
    if ($LASTEXITCODE -ne 0) { throw 'No se pudieron leer las variables del servicio.' }
    $existing = @(($json | ConvertFrom-Json).PSObject.Properties.Name)

    function New-Secret([int]$bytes) {
        # Compatible con Windows PowerShell 5.1 (.NET Framework no tiene RandomNumberGenerator.GetBytes(int)).
        $buffer = New-Object byte[] $bytes
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
        [Convert]::ToBase64String($buffer)
    }
    function Set-Secret([string]$name, [string]$value) {
        # Por stdin, para que el valor no quede en el historial ni en la lista de procesos. Se escribe desde .NET al
        # binario de la CLI con la codificación de entrada en UTF-8 sin BOM: si la consola usa UTF-8 con BOM, tanto
        # el pipe de PowerShell como Process.Start lo escriben primero y el valor queda con U+FEFF adelante.
        $cli = if ($env:_ -and (Test-Path $env:_)) { $env:_ } else { (Get-Command railway -CommandType Application).Source }
        $psi = New-Object System.Diagnostics.ProcessStartInfo $cli
        $psi.Arguments = "variable set $name --stdin --service $Service --skip-deploys"
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $previous = [Console]::InputEncoding
        try {
            [Console]::InputEncoding = New-Object System.Text.UTF8Encoding $false
            $process = [System.Diagnostics.Process]::Start($psi)
            $process.StandardInput.Write($value)
            $process.StandardInput.Close()
        }
        finally { [Console]::InputEncoding = $previous }
        $null = $process.StandardOutput.ReadToEnd()
        $errors = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "No se pudo cargar ${name}: $errors" }
        Write-Host "Generado: $name"
    }

    $created = @()
    if ('Confidential__LocalKey' -notin $existing) { Set-Secret 'Confidential__LocalKey' (New-Secret 32); $created += 'Confidential__LocalKey' }
    if ('Jwt__SigningKey' -notin $existing) { Set-Secret 'Jwt__SigningKey' (New-Secret 48); $created += 'Jwt__SigningKey' }
    if ('Bootstrap__AdminPassword' -notin $existing) {
        Set-Secret 'Bootstrap__AdminPassword' ('Aa1' + (New-Secret 18).Replace('+', 'x').Replace('/', 'y').TrimEnd('='))
        $created += 'Bootstrap__AdminPassword'
    }
    if ('Bootstrap__AdminEmail' -notin $existing) {
        if (-not $AdminEmail) { throw 'Primer despliegue: falta -AdminEmail.' }
        railway variable set "Bootstrap__AdminEmail=$AdminEmail" --service $Service --skip-deploys *> $null
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo cargar Bootstrap__AdminEmail.' }
    }

    # 3. Dominios públicos generados por Railway (*.up.railway.app), una sola vez. Antes de subir el servidor: su
    #    CORS toma el dominio del servicio web por referencia.
    foreach ($name in @($Service, $WebService)) {
        $domains = railway domain list --service $name 2>$null | Out-String
        if ($domains -notmatch 'railway\.app') {
            railway domain --service $name --port 8080
            if ($LASTEXITCODE -ne 0) { throw "No se pudo generar el dominio de $name." }
        }
    }

    # 4. Código: Railway construye con el Dockerfile, corre preDeploy y, si el health check pasa, cambia de versión.
    #    Primero el servidor (CORS y relay), después el sitio que los usa.
    $commit = (git rev-parse --short HEAD).Trim()
    if (git status --porcelain) { Write-Warning "Hay cambios sin commitear: lo que se sube no es exactamente $commit." }
    $targets = switch ($Only) { 'server' { @($Service) } 'web' { @($WebService) } default { @($Service, $WebService) } }
    foreach ($name in $targets) {
        Write-Output "Subiendo $commit a $name..."
        railway up --service $name --ci
        if ($LASTEXITCODE -ne 0) { throw "Falló el build o el despliegue. Ver: railway logs --service $name" }
    }

    if ($created) {
        Write-Warning ("Secretos nuevos: $($created -join ', '). Sellarlos ahora en el panel (Variables, menú de cada " +
            'una, Seal) y guardar una copia de Confidential__LocalKey fuera de Railway: sin ella, los secretos ' +
            'guardados no se recuperan. Ver infra/railway/README.md.')
    }
    Write-Output 'Listo.'
}
finally {
    Pop-Location
}
