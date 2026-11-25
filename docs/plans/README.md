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
| 08 | [Autenticación por App](08-auth-por-app.md) | ✅ Implementado (etapas 1 y 2 en el Ticket-017, etapa 3 en el Ticket-025, etapa 4 en el Ticket-029) | — |
| 09 | [Cliente web](09-cliente-web.md) | ✅ Implementado | — |
| 10 | [gRPC](10-grpc.md) | ✅ Implementado (Ticket-035), sin probar en la app de escritorio | Probar en la app de escritorio contra un servidor gRPC real; al publicar, servidor primero y después `Clients:MinVersion` |
| 11 | [Variables por App](11-variables-por-app.md) | 📝 Propuesto (Ticket-018) | Aprobar el plan (decisiones V5 a V10); al publicar, servidor primero y después `Clients:MinVersion` |

Orden de ejecución: 02 → 03 → 04 → 05 → 06 → 07.

**v0.1.0**, primer release estable, cierra los planes 02 a 07 (Ticket-011). El despliegue y el tiempo real del Plan 05 se validaron en Railway (Ticket-022); el despliegue en AWS sigue pendiente en el Ticket-004.

**v0.2.0** suma lo hecho desde la v0.1.0: consola por request, autocompletar de variables, formato del body, menú contextual en la respuesta, interfaz en inglés y español, autenticación por App con variables de sesión y login automático (plan 08). Como un cliente anterior no entiende la auth `Inherit`, hay que desplegar primero el servidor y después subir `Clients:MinVersion` a `0.2.0`.

**v0.3.0** suma lo hecho desde la v0.2.0: cliente web (plan 09), renovación automática del token con el login de la App (Ticket-025), logins automáticos compartidos entre Apps (Ticket-029), tooltip con el valor de las variables (Ticket-030) y mejoras en la ventana de environments: buscador, columna de la clave más ancha, foco en la variable nueva y el environment activo seleccionado al abrir (Tickets 026, 027, 028 y 031). La migración `SharedLogins` pasa el login de cada App a un login compartido y un cliente anterior no conoce ese tipo de cambio: hay que desplegar primero el servidor y después subir `Clients:MinVersion` a `0.3.0`.

**Próxima versión** (sin publicar): pantalla de servidores guardados (Ticket-033), duplicar (Ticket-034), gRPC (plan 10, Ticket-035) y WebSocket (Ticket-036). La migración `ProtoSets` y los tipos nuevos (`grpc` y `webSocket` en el spec, el cambio `ProtoSet`) no los entiende un cliente anterior: hay que desplegar primero el servidor y después subir `Clients:MinVersion` a la versión nueva.

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
