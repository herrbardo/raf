# Guía para agentes — RAF

RAF (Requests and Friends) es un cliente desktop colaborativo para enviar requests HTTP, con backend propio. Stack, comandos y estructura: [README.md](README.md).

## Reglas obligatorias

El repositorio es público. Antes de escribir código, documentación o commits, leer y cumplir [CLAUDE.md](CLAUDE.md): **nada de claves ni de nombres de infraestructura existente**.

## Idioma

Al usuario se le escribe siempre en español rioplatense: respuestas, reportes, preguntas y resúmenes. Los textos de la UI, la documentación y los mensajes de commit también van en español.

## Dónde está el trabajo

- Hoja de ruta, orden de ejecución y decisiones: [docs/plans/README.md](docs/plans/README.md).
- Cada plan: `docs/plans/NN-nombre.md`.

## Validar cambios

```bash
dotnet build
dotnet test     # necesita Docker: levanta Postgres con Testcontainers
```

## Skills

Están en `.agents/skills/<nombre>/SKILL.md`. Leer el skill completo antes de aplicarlo.

| Skill | Cuándo usarlo |
|---|---|
| [procesar-trabajo](.agents/skills/procesar-trabajo/SKILL.md) | Cuando el usuario pide procesar, ejecutar o resolver un plan o un ticket, o escribe `CPS` |
| [avalonia](.agents/skills/avalonia/SKILL.md) | Al escribir o modificar la UI en `src/Raf.Client`, o al mejorar el aspecto de la aplicación |

## Siglas del usuario

| Sigla | Significado |
|---|---|
| **CPS** | **Commit, Push y Siguiente**: confirma el trabajo actual, lo commitea, lo pushea y pasa al siguiente plan o ticket. El detalle está en [procesar-trabajo](.agents/skills/procesar-trabajo/SKILL.md) |
