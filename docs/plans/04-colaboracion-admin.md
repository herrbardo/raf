# Plan 04 — Colaboración y administración

Estado: **implementado y probado** (Ticket-003). Depende de: Plan 03.
Checkpoint humano al final: probar flujos de admin y permisos con varios usuarios.

Los endpoints de administración y miembros ya existen desde el Plan 01. Este plan agrega las pantallas y el cifrado de secretos.

## Alcance

**Gestión del servidor (solo Admin)**

1. Usuarios: listar, buscar, crear, editar, resetear contraseña, deshabilitar y habilitar, dar o quitar Admin.
2. La contraseña temporal se muestra **una sola vez**, con botón de copiar.
3. Workspaces: listar todos y reasignar Owner.

**Gestión del workspace (solo Owner)**

4. Renombrar y eliminar, con confirmación escribiendo el nombre.
5. Miembros: buscar usuarios, agregar, quitar y cambiar rol.
6. Transferir la propiedad, y opción de salir del workspace para los demás miembros.

**Permisos en la UI**

7. Cada acción se oculta o deshabilita según el rol, en todas las pantallas.
8. Si el rol cambia mientras la app está abierta, la UI se actualiza con la siguiente sincronización.
9. Variables secretas: enmascaradas por defecto, con botón de mostrar para Editor y Owner.

**Cifrado de secretos en el servidor**

10. **`IConfidentialManager`** en el servidor, sin detalles de ningún proveedor:
    - `ProtectAsync(workspaceId, plaintext)` y `UnprotectAsync(workspaceId, ciphertext)`.
    - El valor se guarda **cifrado en la misma fila de la variable**, así la escritura sigue siendo una sola transacción con `Version` y `ChangeSeq`.
    - Una clave de datos por workspace, guardada cifrada en la base (tabla nueva) y mantenida unos minutos en memoria.
11. **Implementación local**, con clave por configuración, solo para desarrollo y tests. El servidor se niega a arrancar con ella en producción. La implementación de AWS va en el Plan 05.
12. Migración de datos que cifra los valores existentes, y manejo del cambio de `isSecret` en ambos sentidos.

## Usuarios deshabilitados: corte inmediato (D3)

- Al validar el JWT, se consulta `IsDisabled` del usuario por clave primaria en cada request, sin caché, para que el corte sea realmente inmediato. La API ya consulta la base en cada request, así que el costo es marginal.
- Un usuario deshabilitado recibe `401` y el cliente vuelve al login.
- Sus conexiones SignalR abiertas se cierran (Plan 05).

## Verificación

- Tests de ViewModels por rol: qué acciones quedan disponibles para Admin, Owner, Editor y Viewer.
- Integración: los secretos nunca se guardan en claro, y siguen funcionando tras la migración.
- Integración: un usuario deshabilitado recibe `401` con un access token todavía vigente.
- Prueba manual con tres usuarios (Owner, Editor, Viewer) y un Admin.

## Decisiones de implementación

- **Cifrado por sobre, con el proveedor separado.** `EnvelopeConfidentialManager` implementa `IConfidentialManager`: genera una clave AES-256 por workspace, la guarda envuelta en `WorkspaceKeys` y la cachea 5 minutos. Los valores se cifran con AES-GCM y el id del workspace como dato asociado, así un valor copiado a otro workspace no se descifra. Lo único que depende del proveedor es `IKeyWrapper` (envolver y desenvolver la clave de datos). En el Plan 05, KMS entra como otro `IKeyWrapper` y no hace falta reescribir el cifrado.
- **Formato guardado:** `enc:v1:` + base64(nonce, tag, texto cifrado). Un valor secreto sin ese prefijo se considera anterior a la migración y se lee tal cual.
- **Clave local:** `Confidential:LocalKey` (32 bytes en base64), generada por `scripts/dev-setup.ps1` en `.env` y user-secrets. Los tests generan una al azar. Con `Confidential:Provider=local` y `ASPNETCORE_ENVIRONMENT=Production`, el servidor no arranca: **hasta el Plan 05 no se puede desplegar en AWS**.
- **Migración de datos:** la tabla nueva llega por migración de EF. Los secretos existentes se cifran justo después de migrar (`ConfidentialSetup.EncryptLegacySecretsAsync`, dentro de `Database:MigrateOnStartup`). Es idempotente y no cambia `Version` ni `ChangeSeq`, porque el valor visible es el mismo.
- **Corte inmediato:** `UserStateCheck` corre en `OnTokenValidated` del JWT. Consulta `IsDisabled` e `IsAdmin` por clave primaria, sin caché. Además del corte por deshabilitación, quitar el rol Admin rige en el acto, sin esperar a que venza el token.
- **Gestión en línea:** los miembros, el nombre del workspace y la administración del servidor no pasan por la bandeja offline. Van directo a la API (`ManagementApi`) y después se sincroniza. Al salir del workspace o eliminarlo, se borra su copia local.
- **Permisos testeables:** la matriz de permisos vive en `WorkspacePermissions` (en `Raf.Client.Core`), que usan los viewmodels. Los tests por rol la cubren ahí, porque los viewmodels dependen de Avalonia y no tienen proyecto de tests.
- **Cambio de rol con la app abierta:** al sincronizar, si cambió el rol en el workspace elegido, se avisa en la barra de estado y se rehacen las pestañas sin cambios. Una pestaña con cambios sin guardar se conserva: si se perdió el permiso, guardar informa el error.
- **Variables secretas (punto 9):** ya venían enmascaradas desde el Plan 02, con el botón "Ver" solo para Editor y Owner. No hubo cambios.
