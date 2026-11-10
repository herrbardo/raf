using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Raf.Server.Confidential;

namespace Raf.Server.Tests;

/// <summary>Qué proveedor de claves acepta el servidor según el entorno (sin base de datos).</summary>
public class ConfidentialSetupTests
{
    private static IServiceProvider Build(string environment, params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(config).AddSingleton(TimeProvider.System);
        services.AddConfidential(config, new HostingEnvironment { EnvironmentName = environment });
        return services.BuildServiceProvider();
    }

    private static readonly (string, string) LocalKey =
        ("Confidential:LocalKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public void En_produccion_el_proveedor_local_no_arranca_sin_habilitarlo()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build(Environments.Production, LocalKey));
        Assert.Contains("AllowLocalKeyInProduction", e.Message);
    }

    [Fact]
    public void En_produccion_el_proveedor_local_arranca_si_se_habilita()
    {
        var sp = Build(Environments.Production, LocalKey, ("Confidential:AllowLocalKeyInProduction", "true"));

        Assert.IsType<LocalKeyWrapper>(sp.GetRequiredService<IKeyWrapper>());
    }

    [Fact]
    public void Habilitarlo_en_produccion_no_reemplaza_la_clave()
    {
        var sp = Build(Environments.Production, ("Confidential:AllowLocalKeyInProduction", "true"));

        var e = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IKeyWrapper>());
        Assert.Contains("Confidential:LocalKey", e.Message);
    }

    [Fact]
    public void Fuera_de_produccion_el_proveedor_local_no_necesita_habilitarse()
    {
        var sp = Build(Environments.Development, LocalKey);

        Assert.IsType<LocalKeyWrapper>(sp.GetRequiredService<IKeyWrapper>());
    }
}
