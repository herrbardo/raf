# Plan 06 — Empaquetado y distribución

Estado: **implementado, falta la prueba humana** (certificados, instalación y actualización reales). Depende de: Plan 02 como mínimo; conviene ejecutarlo al final.
Checkpoint humano al final: aportar certificados y probar instalación y actualización en ambos sistemas.

## Alcance

1. **Velopack** integrado en `Raf.Client`, con arranque y aplicación de actualizaciones.
2. **Instaladores:** Windows x64 (`Setup.exe`) y macOS arm64 y x64 (`.pkg`).
3. **GitHub Actions:** un workflow que, al crear un tag `v*`, compila en runners de Windows y macOS, firma, empaqueta y publica en GitHub Releases.
4. **Auto-update:** la app busca versiones nuevas al iniciar, descarga en segundo plano y muestra un aviso de "Reiniciar para actualizar" (D6). Si el usuario no reinicia, se aplica la próxima vez que abra la app.
5. **Compatibilidad cliente-servidor:** la API expone su versión y la versión mínima de cliente. Un cliente demasiado viejo pide actualizarse en lugar de fallar.
6. **Identidad de la app:** nombre, ícono y metadatos.

## Publicación de actualizaciones (D4)

**GitHub Releases, con el repo público.** El workflow publica con el `GITHUB_TOKEN` propio de Actions: no hace falta infraestructura ni credenciales de AWS.

Los binarios no incluyen la URL del servidor: la indica cada usuario al configurar la app. Los secretos de firma solo existen en GitHub y no se exponen a pull requests de forks.

## Firma

| Plataforma | Requisito | Sin esto |
|---|---|---|
| Windows | Certificado de firma de código o Azure Trusted Signing | SmartScreen advierte al instalar |
| macOS | Cuenta Apple Developer (USD 99/año), certificado Developer ID y notarización | Gatekeeper bloquea la app |

Los pasos de firma se activan solo si existen los secretos en GitHub, así el workflow funciona desde antes de tener los certificados.

La cuenta de Apple Developer todavía no existe: hasta tenerla, el workflow publica solo Windows. Los builds de macOS se activan cuando estén cargados sus secretos.

## Verificación

- Instalación limpia en Windows y macOS.
- Actualización de una versión a la siguiente en ambos sistemas.
- Desinstalación sin dejar binarios. Los datos locales se conservan salvo que el usuario pida borrarlos.
- Cliente con versión menor a la mínima: muestra el aviso de actualización.

## Decisiones de implementación

- **Id del paquete: `RequestsAndFriends`.** Velopack instala en `%LOCALAPPDATA%\<id>` y borra esa carpeta al desinstalar. Con el id `RAF` coincidiría con la carpeta de datos (`%LOCALAPPDATA%\RAF`) y la desinstalación se llevaría la caché, el historial y la sesión. El ejecutable y el título visible siguen siendo `RAF`.
- **Versión:** `Directory.Build.props` tiene `0.1.0` para los builds locales. El workflow la reemplaza con el tag (`v1.2.3` → `1.2.3`); un tag que no es una versión semántica corta el release.
- **Origen de las actualizaciones:** la URL del repositorio no está en el código. El workflow la pasa al compilar (`-p:RafUpdateUrl=...`, desde `github.repository`) y queda como metadato del ensamblado. Un build local no la tiene: la app no busca actualizaciones.
- **Aviso de actualización:** una franja arriba de la ventana, en cualquier pantalla, con "Reiniciar para actualizar". Si se cierra la app sin reiniciar, `VelopackApp.Run()` aplica la versión descargada en el próximo arranque.
- **Compatibilidad (`Clients:MinVersion`):** el cliente manda `X-Raf-Client-Version` en cada llamada, también en el WebSocket. Si es menor a la mínima, el servidor responde **426** antes de autenticar, en cualquier ruta salvo `/health`. El cliente lo detecta en un `DelegatingHandler`, así que también se entera durante la sincronización de fondo o la renovación del token: deja de sincronizar y muestra "Hay que actualizar RAF", con "Actualizar ahora" si la app se instaló con el instalador o un link a la página de releases si no. Una llamada sin la cabecera (curl, scripts) no se controla. La etiqueta de prerelease no se compara: `2.1.0-beta.1` cuenta como `2.1.0`. `GET /version` informa la versión del servidor y la mínima, sin autenticación. En AWS es el parámetro `MinClientVersion` del stack.
- **macOS:** cada arquitectura (`osx-arm64`, `osx-x64`) es un canal propio de Velopack, para que una app no descargue el paquete de la otra. El job corre solo si existen los tres secretos de Apple obligatorios; si no, el release queda solo con Windows y el workflow lo avisa.
- **Firma en Windows:** con un certificado `.pfx` (signtool). Azure Trusted Signing también lo soporta `vpk` (`--azureTrustedSignFile`), pero requiere login en Azure desde el workflow: se agrega si se elige esa opción.
- **Ícono:** provisorio, en `src/Raf.Client/Assets` (`raf.ico`, `raf.icns` y `raf.png`). Para cambiarlo alcanza con reemplazar esos tres archivos.
- **Verificado en local:** `dotnet publish` para `win-x64` y `vpk pack` generan `RequestsAndFriends-win-Setup.exe`, el paquete completo y el portable. La versión y la URL de actualizaciones quedan en el binario, y la app publicada arranca. No se probó la instalación ni el workflow en GitHub. Las opciones de `vpk` para macOS no se pueden comprobar desde Windows: el primer release con los secretos de Apple es la prueba.

## Secretos del repositorio

Se cargan en *Settings → Secrets and variables → Actions*. Ninguno es obligatorio para publicar Windows sin firmar.

| Secreto | Contenido |
|---|---|
| `WINDOWS_CERTIFICATE_PFX` | Certificado de firma de código `.pfx`, en base64 |
| `WINDOWS_CERTIFICATE_PASSWORD` | Contraseña del `.pfx` |
| `MACOS_APP_CERTIFICATE_P12` | Certificado *Developer ID Application* `.p12`, en base64 |
| `MACOS_INSTALLER_CERTIFICATE_P12` | Certificado *Developer ID Installer* `.p12`, en base64 |
| `MACOS_CERTIFICATE_PASSWORD` | Contraseña de los dos `.p12` |
| `MACOS_APP_IDENTITY` | Nombre de la identidad, por ejemplo `Developer ID Application: <tu-nombre> (<team-id>)` |
| `MACOS_INSTALLER_IDENTITY` | Nombre de la identidad, por ejemplo `Developer ID Installer: <tu-nombre> (<team-id>)` |
| `MACOS_NOTARY_APPLE_ID` | Apple ID con el que se notariza |
| `MACOS_NOTARY_TEAM_ID` | Team ID de la cuenta de Apple Developer |
| `MACOS_NOTARY_PASSWORD` | Contraseña específica de app para ese Apple ID |

Para pasar un archivo a base64 en PowerShell: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx")) | Set-Clipboard`.

## Requiere del usuario

- Certificado de Windows y cuenta Apple Developer, cargados como secretos del repositorio.
- Ícono y nombre definitivos.
