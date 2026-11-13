# Hoja de ruta — RAF (Requests and Friends)

Cada plan termina en un **checkpoint humano**: el punto donde hace falta una decisión, credencial o acción tuya. Dentro de un plan, el trabajo avanza sin intervención.

| # | Plan | Estado | 🧑 Checkpoint humano al final |
|---|---|---|---|
| 00 | [Arquitectura y stack](00-arquitectura.md) | ✅ Aprobado | — |
| 01 | [Esqueleto + backend base](01-backend-base.md) | ✅ Implementado | — |
| 02 | [Cliente base](02-cliente-base.md) | ✅ Implementado | — |
| 03 | [Sincronización](03-sincronizacion.md) | ✅ Implementado | — |
| 04 | [Colaboración y administración](04-colaboracion-admin.md) | ✅ Implementado | — |
| 05 | [Despliegue AWS y tiempo real](05-despliegue-aws.md) | ✅ Implementado, validado en Railway | Desplegar en AWS y validar (Ticket-004, no bloquea el release) |
| 06 | [Empaquetado y distribución](06-distribucion.md) | ✅ Implementado | — |
| 07 | [Embellecer la aplicación](07-embellecer-ui.md) | ✅ Implementado | — |
| 08 | [Autenticación por App](08-auth-por-app.md) | 🚧 Etapas 1 y 2 implementadas (Ticket-017) | Etapa 3 en el Ticket-025 |

Orden de ejecución: 02 → 03 → 04 → 05 → 06 → 07.

**v0.1.0**, primer release estable, cierra los planes 02 a 07 (Ticket-011). El despliegue y el tiempo real del Plan 05 se validaron en Railway (Ticket-022); el despliegue en AWS sigue pendiente en el Ticket-004.

## Decisiones pendientes

| # | Decisión | Afecta | Recomendación |
|---|---|---|---|
| D1 | Tiempo real | Plan 05 | ✅ **SignalR por el ALB** |
| D2 | Secretos en el cliente y en el servidor | Planes 03, 04 y 05 | ✅ **Cliente:** cifrados en la caché local con DPAPI/Keychain. **Servidor:** cifrados en Postgres con KMS, detrás de `IConfidentialManager` |
| D3 | Usuario deshabilitado | Plan 04 | ✅ **Se corta al instante** |
| D4 | Actualizaciones del cliente | Plan 06 | ✅ **GitHub Releases**, con el repo público |
| D6 | Aplicación de actualizaciones y Mac | Plan 06 | ✅ Descarga en segundo plano y aviso de "Reiniciar para actualizar". Mac entra con la cuenta Apple Developer: se publica firmado desde el Ticket-009 |
| D5 | Alcance de la primera versión | Planes 02 y 06 | ✅ `multipart` con archivos e importación de Postman quedan **para después** |
| D7 | Tema principal del diseño | Plan 07 | ✅ **Oscuro primero**, con claro bien soportado |
| D8 | Firma del instalador de Windows | Plan 06 | ✅ **Sin firmar** por ahora: SmartScreen advierte al instalar. El paso de firma del workflow queda listo por si se retoma |
