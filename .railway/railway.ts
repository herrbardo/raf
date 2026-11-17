// Infraestructura de RAF en Railway: el servidor y su base PostgreSQL. Se aplica con `railway config apply`
// (ver infra/railway/README.md). Los secretos no están acá: `preserve()` conserva el valor cargado en Railway,
// que infra/railway/deploy.ps1 genera la primera vez y después se sella desde el panel.
import { defineRailway, postgres, preserve, service } from "railway/iac";

export default defineRailway((ctx, project) => {
  const db = postgres("postgres");

  // Cliente web: archivos estáticos (WebAssembly) servidos por Caddy. Se conecta al servidor que indique cada
  // usuario; el servidor de este proyecto le habilita CORS más abajo.
  const web = service("web", {
    build: { builder: "DOCKERFILE", dockerfilePath: "src/Raf.Client.Browser/Dockerfile" },
    healthcheck: "/health",
    replicas: 1,
    env: {
      PORT: "8080",
    },
  });

  const server = service("server", {
    build: { builder: "DOCKERFILE", dockerfilePath: "src/Raf.Server/Dockerfile" },
    // Antes de que la versión nueva atienda: migra, cifra los secretos heredados y crea el admin. Si falla, el
    // despliegue se corta y sigue atendiendo la versión anterior.
    preDeploy: "dotnet Raf.Server.dll --migrate",
    healthcheck: "/health",
    healthcheckTimeout: 120,
    replicas: 1,
    env: {
      // La imagen de ASP.NET escucha en 8080; PORT le dice a Railway a dónde mandar el tráfico.
      PORT: "8080",
      ASPNETCORE_ENVIRONMENT: "Production",
      Database__MigrateOnStartup: "false",

      // Base por la red privada del proyecto, que Railway ya cifra. Prefer usa TLS si la base lo ofrece (su
      // certificado es autofirmado, así que no se valida).
      Db__Host: db.env.PGHOST,
      Db__Port: db.env.PGPORT,
      Db__Name: db.env.PGDATABASE,
      Db__User: db.env.PGUSER,
      Db__Password: db.env.PGPASSWORD,
      Db__SslMode: "Prefer",

      // Sin KMS: la clave maestra es una variable sellada (decisión del Ticket-022).
      Confidential__Provider: "local",
      Confidential__AllowLocalKeyInProduction: "true",
      Confidential__LocalKey: preserve(),

      // El sitio del servicio "web" puede usar la API desde el navegador. Es una referencia: el dominio no queda
      // escrito acá (llega sin esquema y el servidor asume https).
      Cors__AllowedOrigins__0: web.env.RAILWAY_PUBLIC_DOMAIN,

      Jwt__SigningKey: preserve(),
      Bootstrap__AdminEmail: preserve(),
      Bootstrap__AdminPassword: preserve(),
    },
  });

  return project(ctx.projectName ?? "raf", { resources: [db, server, web] });
});
