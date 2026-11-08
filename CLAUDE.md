# Reglas del repositorio

Este repositorio es público. Estas reglas valen para código, templates, tests, scripts, documentación y mensajes de commit.

## MUY IMPORTANTE: nada de claves ni de nombres de infraestructura existente

**Nunca versionar:**

- Contraseñas, tokens, claves de firma o de API, cadenas de conexión con credenciales ni certificados, tampoco los "solo para desarrollo".
- Identificadores de infraestructura que ya existe: IDs de cuenta, ARNs, IDs de VPC, subredes o security groups, nombres de ALB, dominios, IPs internas, nombres de perfiles de AWS y de organizaciones.

**En su lugar:**

- **CloudFormation:** todo recurso que ya exista se recibe por parámetro, sin valor por defecto real (como `VpcId` o `AlbListenerArn` en `infra/raf.yaml`). Los secretos se generan en Secrets Manager y se leen por referencia, nunca como parámetro en texto plano.
- **Desarrollo local:** los valores se generan con `scripts/dev-setup.ps1` y quedan en `.env` y en `dotnet user-secrets`, ambos fuera del repo.
- **Tests:** las claves se generan al azar en cada corrida.
- **Producción:** Secrets Manager, inyectado como variables de entorno.
- **URL del servidor:** no va compilada en el cliente; la indica cada usuario.
- **Documentación y ejemplos:** solo marcadores evidentes, como `<tu-perfil>`, `vpc-xxxxxxxx` o la cuenta `123456789012`.

**Si algo de esto llega a un commit**, borrarlo del código no alcanza: queda en el historial. Hay que rotar la credencial y limpiar el historial.

Antes de cada commit, revisar el diff buscando estos datos.
