# Tipo
FEATURE

# Descripción
Desplegar el backend en AWS y validarlo (checkpoint del Plan 05). Desplegar el stack de infra/raf.yaml con AWS CLI, con los parámetros de la VPC y el ALB existentes, y validar: dos clientes contra el entorno desplegado ven los cambios en tiempo real sin refrescar, el cliente se recupera de un corte de conexión, una versión nueva se despliega sin corte con la migración aplicada antes y los secretos quedan cifrados en RDS.

Recordatorio: ningún identificador de la infraestructura real va al repo (CLAUDE.md).

Plan: docs/plans/05-despliegue-aws.md

# Estado
CREADO
