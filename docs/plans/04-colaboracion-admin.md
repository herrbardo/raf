# Plan 04 — Colaboración y administración

Estado: **definido, sin ejecutar**. Depende de: Plan 03.
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
