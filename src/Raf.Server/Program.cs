using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Raf.Server.Admin;
using Raf.Server.Auth;
using Raf.Server.Data;
using Raf.Server.Infrastructure;
using Raf.Server.Workspaces;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

// ---------- Datos ----------
services.AddHttpContextAccessor();
services.AddScoped<ICurrentUser, HttpCurrentUser>();
services.AddDbContext<RafDbContext>((sp, o) =>
    o.UseNpgsql(DatabaseConfig.GetConnectionString(sp.GetRequiredService<IConfiguration>())));

// ---------- Identity + JWT ----------
services.AddIdentityCore<AppUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddEntityFrameworkStores<RafDbContext>();

services.AddSingleton(TimeProvider.System);
services.AddScoped<TokenService>();
services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.Section).ValidateDataAnnotations().ValidateOnStart();

services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((o, jwt) =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Value.Issuer,
            ValidAudience = jwt.Value.Audience,
            IssuerSigningKey = jwt.Value.GetKey(),
            NameClaimType = RafClaims.Name,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

services.AddAuthorizationBuilder()
    // Por defecto: autenticado y sin contraseña temporal pendiente de cambio.
    .SetDefaultPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => !ctx.User.HasClaim(c => c.Type == RafClaims.PasswordChange))
        .Build())
    .AddPolicy(Policies.AllowPasswordChange, p => p.RequireAuthenticatedUser())
    .AddPolicy(Policies.Admin, p => p
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => !ctx.User.HasClaim(c => c.Type == RafClaims.PasswordChange))
        .RequireClaim(RafClaims.Admin, "true"));

// ---------- HTTP ----------
services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
services.AddProblemDetails();
services.AddExceptionHandler<DbExceptionHandler>();
services.AddHealthChecks();
services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "RAF — Requests and Friends API";
    doc.Components ??= new OpenApiComponents();
    doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
    doc.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
    };
    doc.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", doc)] = [] }];
    return Task.CompletedTask;
}));

var app = builder.Build();

// Detrás de un ALB compartido la app vive bajo un subpath (p. ej. /raf): el ALB no lo recorta,
// así que se quita acá. Las rutas sin prefijo siguen funcionando (desarrollo local).
if (app.Configuration["PathBase"] is { Length: > 0 } pathBase) app.UsePathBase(pathBase);
app.UseRouting();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(o => o.AddPreferredSecuritySchemes("Bearer"));
}

app.MapHealthChecks("/health");
app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapWorkspaceEndpoints().MapContentEndpoints();

await app.InitializeAsync();
await app.RunAsync();

public partial class Program;
