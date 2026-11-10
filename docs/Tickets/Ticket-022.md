# Tipo
FEATURE

# Descripción
Desplegar el backend en Railway, como alternativa a AWS (Ticket-004), con la infraestructura definida en código dentro del repo y desplegada con la CLI de Railway (ya instalada y con la cuenta configurada en el equipo del usuario).

Alcance:
- Servicio del servidor construido desde src/Raf.Server/Dockerfile y una base PostgreSQL de Railway.
- Configuración como código (railway.json o railway.toml) y un script o guía de despliegue, al estilo de infra/deploy.ps1 e infra/README.md.
- Migraciones antes de que la versión nueva atienda (dotnet Raf.Server.dll --migrate), sin correrlas desde cada instancia.
- Secretos (clave JWT, contraseña inicial del admin, credenciales de la base) como variables de Railway, nunca en el repo.
- Health check en /health, WebSocket de /hubs/changes funcionando y PathBase según el dominio que asigne Railway.

Decisión pendiente: el servidor exige Confidential:Provider=kms en producción y rechaza 'local' (LocalKeyWrapper). Railway no tiene KMS. Hay que elegir antes de implementar: usar AWS KMS desde Railway con credenciales IAM acotadas, o agregar un proveedor que tome la clave maestra de una variable de entorno de Railway y aceptarlo en producción.

Validar lo mismo que en el Ticket-004: dos clientes ven los cambios en tiempo real sin refrescar, el cliente se recupera de un corte, una versión nueva se despliega con la migración aplicada antes y los secretos quedan cifrados en la base.

Recordatorio: ningún identificador del proyecto de Railway (IDs, dominios, tokens) va al repo (CLAUDE.md).

# Estado
CREADO
