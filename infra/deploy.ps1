# Despliega una versión nueva del servidor: construye y sube la imagen, migra la base con una tarea de una sola
# ejecución y recién entonces actualiza el servicio. No guarda credenciales: usa el perfil de AWS CLI activo.
#
# Uso (desde la raíz del repo, con el stack ya creado; ver infra/README.md):
#   $env:AWS_PROFILE = "<tu-perfil>"; $env:AWS_REGION = "us-east-1"
#   .\infra\deploy.ps1 -Stack raf-dev                  # versión nueva, mismas tareas
#   .\infra\deploy.ps1 -Stack raf-dev -DesiredCount 1  # primer despliegue: enciende el servicio
param(
    [Parameter(Mandatory)][string]$Stack,
    [int]$DesiredCount = -1,
    [string]$Tag
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$template = Join-Path $PSScriptRoot 'raf.yaml'

function Invoke-Checked([string]$what, [scriptblock]$command) {
    $output = & $command
    if ($LASTEXITCODE -ne 0) { throw "Falló: $what (código $LASTEXITCODE)." }
    return $output
}

function Get-Output([string]$key) {
    $value = Invoke-Checked "leer el output $key" {
        aws cloudformation describe-stacks --stack-name $Stack --query "Stacks[0].Outputs[?OutputKey=='$key'].OutputValue" --output text
    }
    if (-not $value -or $value -eq 'None') { throw "El stack $Stack no tiene el output $key. ¿Se actualizó el template?" }
    return $value.Trim()
}

function Update-Stack([string[]]$overrides) {
    Write-Output "Actualizando el stack: $($overrides -join ' ')"
    aws cloudformation deploy --template-file $template --stack-name $Stack --capabilities CAPABILITY_IAM `
        --no-fail-on-empty-changeset --parameter-overrides @overrides
    if ($LASTEXITCODE -ne 0) { throw "Falló la actualización del stack." }
}

if (-not $Tag) {
    $Tag = (git -C $root rev-parse --short HEAD).Trim()
    if (git -C $root status --porcelain) { Write-Warning "Hay cambios sin commitear: la imagen $Tag no corresponde exactamente a ese commit." }
}

# 1. Imagen
$repo = Get-Output 'RepositoryUri'
Write-Output "Construyendo ${repo}:$Tag"
aws ecr get-login-password | docker login --username AWS --password-stdin $repo.Split('/')[0]
if ($LASTEXITCODE -ne 0) { throw 'No se pudo iniciar sesión en ECR.' }
docker build -f (Join-Path $root 'src/Raf.Server/Dockerfile') -t "${repo}:$Tag" $root
if ($LASTEXITCODE -ne 0) { throw 'Falló docker build.' }
docker push "${repo}:$Tag"
if ($LASTEXITCODE -ne 0) { throw 'Falló docker push.' }

# 2. Migraciones, antes de que el servicio use la imagen nueva
Update-Stack @("MigrationImageTag=$Tag")
$cluster = Get-Output 'ClusterName'
$taskDef = Get-Output 'MigrationTaskDefinitionArn'
$subnets = Get-Output 'ServiceSubnetIds'
$securityGroup = Get-Output 'ServiceSecurityGroupId'
$publicIp = Get-Output 'AssignPublicIp'
$network = "awsvpcConfiguration={subnets=[$subnets],securityGroups=[$securityGroup],assignPublicIp=$publicIp}"

Write-Output 'Ejecutando la tarea de migraciones...'
$taskArn = Invoke-Checked 'lanzar la tarea de migraciones' {
    aws ecs run-task --cluster $cluster --task-definition $taskDef --launch-type FARGATE `
        --network-configuration $network --query 'tasks[0].taskArn' --output text
}
$taskArn = $taskArn.Trim()
Invoke-Checked 'esperar la tarea de migraciones' { aws ecs wait tasks-stopped --cluster $cluster --tasks $taskArn } | Out-Null
$exit = Invoke-Checked 'leer el resultado de la migración' {
    aws ecs describe-tasks --cluster $cluster --tasks $taskArn --query 'tasks[0].containers[0].exitCode' --output text
}
if ($exit.Trim() -ne '0') {
    $reason = aws ecs describe-tasks --cluster $cluster --tasks $taskArn --query 'tasks[0].stoppedReason' --output text
    throw "La migración terminó con código '$($exit.Trim())' ($reason). El servicio sigue con la versión anterior. " +
        "Ver logs: aws logs tail $(Get-Output 'LogGroupName') --log-stream-name-prefix migrate"
}
Write-Output 'Migraciones aplicadas.'

# 3. Servicio
$overrides = @("ImageTag=$Tag")
if ($DesiredCount -ge 0) { $overrides += "DesiredCount=$DesiredCount" }
Update-Stack $overrides
Write-Output "Listo: el servicio quedó en la imagen $Tag."
