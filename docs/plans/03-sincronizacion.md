# Plan 03 — Sincronización

Estado: **implementado y probado** (Ticket-002). Depende de: Plan 02.
Checkpoint humano al final: probar con 2 clientes y 2 usuarios en simultáneo.

## Alcance

**Servidor**

1. `GET /workspaces/{ws}/changes?since={seq}&limit=N`: devuelve las entidades con `ChangeSeq > since`, incluidas las borradas, paginadas, y el último `seq` entregado.
2. **Creación idempotente:** hoy un `POST` con un `id` existente devuelve 409. Si el cliente reintenta porque perdió la respuesta, debe recibir el elemento ya creado.
3. **`Version` en miembros:** `MemberDto` la expone y los cambios de rol quedan incluidos en el feed.
4. `IChangeNotifier`, invocado después de confirmar cada transacción.

**Cliente**

5. **Caché SQLite por usuario y servidor** (EF Core), como nueva implementación de `IWorkspaceStore`.
6. **Bandeja de salida (outbox):** cada edición se aplica localmente y se encola. Se envía en orden, respetando dependencias: padre antes que hijo al crear, hijo antes que padre al borrar.
7. **Motor de sync:** pull por `since` y luego push. Se dispara al arrancar, al guardar, al reconectar, al recuperar el foco, al recibir un aviso y cada pocos minutos.
8. **Conflictos:** ante un `409`, pantalla con versión local y remota; el usuario elige una o edita.
9. `IChangeSubscriber` con polling. El tiempo real va en el Plan 05.
10. Indicador de estado: sincronizado, pendiente, sin conexión, en conflicto.

## Casos borde que el plan debe resolver

| Caso | Comportamiento |
|---|---|
| Creo un hijo offline y otro usuario borró el padre | El push falla con 400: se marca como conflicto y se ofrece recrear o descartar. |
| Variable con clave duplicada creada offline | El `409` de unicidad se muestra como conflicto, no como error genérico. |
| Me quitan de un workspace | Se borra su caché local y sus cambios pendientes, avisando antes si había pendientes. |
| Se elimina el workspace | Igual que el caso anterior. |
| Paso de Viewer a Editor | **Resincronización completa de variables**: los secretos que antes llegaban sin valor no cambiaron, así que no aparecen en el feed. |
| Paso de Editor a Viewer | Se borran los valores secretos de la caché y se resincroniza. |
| Cambia `isSecret` de una variable | Entra en el feed por su `ChangeSeq`; sin caso especial. |

## Decisiones de implementación

- La regla de conflicto se aplica **por elemento completo**, sin fusionar campos.
- **Valores secretos en disco:** cifrados con una clave protegida por DPAPI/Keychain (D2), para poder ejecutar requests sin conexión.
- El feed respeta el rol: un Viewer recibe los secretos sin valor.
- La bandeja guarda qué cambió, no los datos: lo que se envía es siempre el estado actual del elemento en la caché.
- **Recrear** asigna ids nuevos al elemento y a sus padres borrados, porque el servidor no deja reutilizar un id eliminado.
- **Descartar** un cambio ya conocido por el servidor reinicia el `since` del workspace y lo vuelve a traer completo.
- Editar una pestaña con una versión superada solo por cambios propios no es un conflicto; superada por otro usuario, sí.

## Verificación

- Integración del feed: paginación, borrados, enmascarado por rol.
- Motor de sync con dos clientes simulados contra el servidor real: edición cruzada, conflicto, reintento idempotente, corte de red a mitad de un push.
- Un test por cada fila de la tabla de casos borde.
