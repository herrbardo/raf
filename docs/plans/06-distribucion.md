# Plan 06 — Empaquetado y distribución

Estado: **definido, sin ejecutar**. Depende de: Plan 02 como mínimo; conviene ejecutarlo al final.
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

## Requiere del usuario

- Certificado de Windows y cuenta Apple Developer, cargados como secretos del repositorio.
- Ícono y nombre definitivos.
