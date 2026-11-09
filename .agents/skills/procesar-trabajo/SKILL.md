---
name: procesar-trabajo
description: "Procesa un plan de docs/plans o un ticket: crea el branch, resuelve los requisitos, valida y espera la confirmación del usuario antes de seguir con el siguiente. Usar cuando el usuario pide procesar, ejecutar o resolver un plan o ticket, o cuando escribe CPS (Commit, Push y Siguiente)."
---

# Procesar plan o ticket

Se trabaja de a un elemento por vez. El ciclo es: **preparar → resolver → validar → esperar confirmación → CPS**.

## 1. Identificar el trabajo

- **Plan:** `docs/plans/NN-nombre.md`. Si el usuario no indica cuál, tomar el primero sin ejecutar según el orden de [docs/plans/README.md](../../../docs/plans/README.md).
- **Ticket:** el texto que pasa el usuario, o un issue de GitHub (`gh issue view <n>`). Si hay varios, la cola es la lista que dio el usuario, en ese orden.

Leer los requisitos completos, incluidas dependencias y decisiones relacionadas. Si una decisión pendiente o un dato faltante cambia lo que hay que construir, preguntar antes de empezar.

## 2. Crear el branch

1. Revisar `git status`. Si hay cambios sin commitear que no son de este trabajo, frenar y preguntar.
2. Elegir la base:
   - `main`, si el trabajo del que depende ya está en `main` o no depende de nada.
   - El branch del elemento anterior, si ese trabajo todavía no se integró a `main`.
3. Crear el branch y avisar cuál es la base:
   - Plan: `plan-NN-nombre` (ejemplo: `plan-04-colaboracion-admin`).
   - Ticket: `ticket-<id>-descripcion-corta`. Sin id, `ticket-descripcion-corta`.

El nombre del branch también es público: no incluir datos que [CLAUDE.md](../../../CLAUDE.md) prohíbe.

## 3. Resolver los requisitos

- Cubrir todo el alcance, sin agregar lo que no se pidió.
- Agregar o ajustar tests para el comportamiento nuevo.
- Si un plan define una sección de verificación, cumplirla.
- En un plan, registrar en su documento las decisiones de implementación que no sean obvias.

## 4. Validar y reportar

1. Correr `dotnet build` y `dotnet test`, y las verificaciones propias del plan o ticket.
2. Comparar lo implementado con cada requisito.
3. En un plan, actualizar el estado en su documento y en la tabla de `docs/plans/README.md`. Si tiene checkpoint humano: `🧪 Implementado, falta la prueba humana`; si no: `✅ Implementado`.
4. Reportar al usuario y **detenerse**:
   - qué se hizo y cómo quedó cada requisito;
   - resultado real de build y tests, incluido lo que falló o no se pudo correr;
   - qué tiene que probar el usuario, con los pasos y el checkpoint humano del plan;
   - la opción de escribir `CPS` para continuar.

No commitear ni avanzar por cuenta propia. Si el usuario pide correcciones, aplicarlas y volver a este paso.

## 5. CPS: Commit, Push y Siguiente

`CPS` es la confirmación del trabajo actual. Commit y push se hacen **solo** con `CPS` o con un pedido explícito de commit y push. Si el usuario aprueba con otras palabras, preguntar si equivale a `CPS`.

1. **Revisar:** leer el diff completo y los archivos nuevos buscando claves e identificadores de infraestructura, como exige [CLAUDE.md](../../../CLAUDE.md). Si aparece alguno, frenar y avisar.
2. **Commit:** agregar los archivos por nombre, solo los de este trabajo.
   - Título: `RAF: <descripción> (Plan NN)` o `RAF: <descripción> (Ticket <id>)`.
   - Cuerpo: resumen breve de lo hecho.
   - Atribución: la que indique el entorno del agente.
3. **Push:** `git push -u origin <branch>`. Sin `--force`, sin merge y sin abrir un PR, salvo que el usuario lo pida. Si el push falla, frenar y avisar: no pasar al siguiente.
4. **Siguiente:** tomar el próximo elemento de la cola y volver al paso 1, hasta volver a detenerse en el paso 4. Si en la cola no queda nada, avisar y terminar.

Si el siguiente plan depende de uno con la prueba humana pendiente, `CPS` alcanza para avanzar: mencionar en el reporte que esa prueba sigue pendiente.
