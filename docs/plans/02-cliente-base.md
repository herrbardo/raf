# Plan 02 — Cliente base

Estado: **implementado, pendiente de la prueba humana**. Depende de: Plan 01.
Checkpoint humano al final: probar la app y dar feedback de UX.

En este plan el cliente trabaja **siempre conectado**, contra la API. La caché local llega en el Plan 03.

## Alcance

1. **Proyectos nuevos:** `Raf.Client.Core` (sin UI) y `Raf.Client` (Avalonia 11, CommunityToolkit.Mvvm, AvaloniaEdit), más `Raf.Client.Core.Tests`.
2. **Conexión:** pantalla para indicar la URL del servidor, incluido el subpath (`https://dominio/raf`). Se recuerda localmente.
3. **Sesión:**
   - Login y cambio obligatorio de la contraseña temporal.
   - Cambio voluntario de contraseña y logout.
   - Tokens guardados con DPAPI (Windows) y Keychain (macOS).
   - Renovación automática del token, con un solo refresh a la vez: el servidor trata la reutilización de un refresh token como robo y cierra todas las sesiones.
4. **Pantalla principal:**
   - Selector de workspace y de environment activo (elección local por workspace).
   - Árbol App → Carpeta → Request → Sample, con crear, renombrar, eliminar y reordenar.
   - Pestañas de requests abiertos, con indicador de cambios sin guardar.
5. **Editor de request:** método, URL, query params, headers, body (`None`, `Json`, `Text`, `Xml`, `FormUrlEncoded`) y auth (`None`, `Bearer`, `Basic`, `ApiKey`).
6. **Editor de sample:** muestra lo heredado y permite sobreescribir o quitar cada campo. Volver a heredar es una acción explícita.
7. **Environments y variables:** editor de globales y por environment. `{{var}}` resaltada en los campos, con aviso si no tiene valor.
8. **Ejecución:**
   - `RequestRunner` en `Raf.Client.Core`: aplica merge del sample, resuelve variables y arma el `HttpRequestMessage`.
   - Cancelación, timeout configurable y opción por request para aceptar certificados inválidos (APIs internas).
   - Respuesta: status, tiempo, tamaño, headers y body con formato para JSON/XML, y vista cruda. Bodies grandes se truncan al mostrarlos, con opción de guardar a archivo.
9. **Historial local** de ejecuciones, por usuario y sin sincronizar. Se guarda sin valores de variables secretas.

## Decisiones de implementación

- `Raf.Client.Core` no depende de Avalonia, para poder testear ejecución y sesión sin UI.
- Acceso a datos detrás de una interfaz (`IWorkspaceStore`): en este plan va directo a la API y el Plan 03 la reemplaza por la caché local sin tocar los ViewModels.
- Un `409` al guardar muestra un aviso y ofrece recargar. La resolución completa de conflictos va en el Plan 03.
- Viewer: la UI queda en solo lectura. Si un request usa un secreto que el Viewer no puede leer, se avisa antes de ejecutar.
- El timeout y la opción de aceptar certificados inválidos son **locales de cada equipo** y no se sincronizan: así nadie le desactiva la validación TLS a otro miembro del equipo.
- El historial guarda método, URL sin valores secretos y resultado. No guarda headers, auth ni bodies.

## Verificación

- Tests de `RequestRunner` contra un servidor HTTP local: cada tipo de body y auth, variables, cancelación y timeout.
- Tests de sesión: renovación concurrente y sesión expirada.
- Compila en Windows y macOS (runner de GitHub Actions).
- Prueba manual del flujo completo en Windows contra el servidor local.
