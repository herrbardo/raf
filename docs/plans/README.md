# Hoja de ruta — RAF (Requests and Friends)

Cada plan termina en un **checkpoint humano**: el punto donde hace falta una decisión, credencial o acción tuya. Dentro de un plan, el trabajo avanza sin intervención.

| # | Plan | Estado | 🧑 Checkpoint humano al final |
|---|---|---|---|
| 00 | [Arquitectura y stack](00-arquitectura.md) | ✅ Aprobado | — |
| 01 | [Esqueleto + backend base](01-backend-base.md) | ✅ Implementado | — |
| 02 | [Cliente base](02-cliente-base.md) | 🧪 Implementado, falta la prueba humana | Probar la app y dar feedback de UX |
| 03 | [Sincronización](03-sincronizacion.md) | 🧪 Implementado, falta la prueba humana | Probar con 2 clientes y 2 usuarios en simultáneo |
| 04 | [Colaboración y administración](04-colaboracion-admin.md) | 🧪 Implementado, falta la prueba humana | Probar flujos de admin y permisos con varios usuarios |
| 05 | [Despliegue AWS y tiempo real](05-despliegue-aws.md) | 📝 Definido (template base hecho) | Desplegar con AWS CLI y validar |
| 06 | [Empaquetado y distribución](06-distribucion.md) | 📝 Definido | Certificados de firma y prueba de instalación |
| 07 | [Embellecer la aplicación](07-embellecer-ui.md) | 📝 Definido | Revisar la aplicación en tema claro y oscuro, y dar feedback visual |

Orden de ejecución: 02 → 03 → 04 → 05 → 06 → 07.

## Decisiones pendientes

| # | Decisión | Afecta | Recomendación |
|---|---|---|---|
| D1 | Tiempo real | Plan 05 | ✅ **SignalR por el ALB** |
| D2 | Secretos en el cliente y en el servidor | Planes 03, 04 y 05 | ✅ **Cliente:** cifrados en la caché local con DPAPI/Keychain. **Servidor:** cifrados en Postgres con KMS, detrás de `IConfidentialManager` |
| D3 | Usuario deshabilitado | Plan 04 | ✅ **Se corta al instante** |
| D4 | Actualizaciones del cliente | Plan 06 | ✅ **GitHub Releases**, con el repo público |
| D6 | Aplicación de actualizaciones y Mac | Plan 06 | ✅ Descarga en segundo plano y aviso de "Reiniciar para actualizar". Mac entra cuando exista la cuenta Apple Developer; hasta entonces se publica solo Windows |
| D5 | Alcance de la primera versión | Planes 02 y 06 | ✅ `multipart` con archivos e importación de Postman quedan **para después** |
| D7 | Tema principal del diseño | Plan 07 | Pendiente. Recomendación: **diseñar primero para oscuro** y mantener claro bien soportado |
