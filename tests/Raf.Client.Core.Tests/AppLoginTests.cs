using Raf.Client.Core.Execution;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Tests;

public class AppLoginTests
{
    private static readonly AppLogin Valid = AppLogin.Default with
    {
        Enabled = true, Request = AppLogin.Default.Request with { Url = "{{base}}/auth/token" },
    };

    [Fact]
    public void Un_login_completo_no_tiene_problemas()
    {
        Assert.Equal(AppLoginProblem.None, Valid.Problem());
        Assert.Equal(AppLoginProblem.None, (Valid with { Expiry = TokenExpiry.Fixed, ExpiryMinutes = 30 }).Problem());
        Assert.Equal(AppLoginProblem.None, (Valid with { Expiry = TokenExpiry.ResponseField, ExpiryPath = "$.expires_in" }).Problem());
    }

    [Theory]
    [InlineData(AppLoginProblem.InheritAuth)]
    [InlineData(AppLoginProblem.InvalidMethod)]
    [InlineData(AppLoginProblem.MissingUrl)]
    [InlineData(AppLoginProblem.MissingTokenPath)]
    [InlineData(AppLoginProblem.InvalidVariable)]
    [InlineData(AppLoginProblem.MissingExpiryPath)]
    [InlineData(AppLoginProblem.InvalidExpiryMinutes)]
    public void Detecta_lo_que_falta_y_lo_explica(AppLoginProblem expected)
    {
        var login = expected switch
        {
            AppLoginProblem.InheritAuth => Valid with { Request = Valid.Request with { Auth = AuthSpec.Inherit } },
            AppLoginProblem.InvalidMethod => Valid with { Request = Valid.Request with { Method = " " } },
            AppLoginProblem.MissingUrl => Valid with { Request = Valid.Request with { Url = "" } },
            AppLoginProblem.MissingTokenPath => Valid with { TokenPath = " " },
            AppLoginProblem.InvalidVariable => Valid with { Variable = "mi token" },
            AppLoginProblem.MissingExpiryPath => Valid with { Expiry = TokenExpiry.ResponseField },
            _ => Valid with { Expiry = TokenExpiry.Fixed, ExpiryMinutes = 0 },
        };

        Assert.Equal(expected, login.Problem());
        Assert.NotEmpty(AppLoginTexts.Describe(expected));
    }

    [Fact]
    public void Apagado_se_acepta_incompleto_pero_nunca_heredando_la_auth()
    {
        var off = Valid with { Enabled = false, TokenPath = "", Variable = "", Request = Valid.Request with { Url = "" } };
        Assert.Equal(AppLoginProblem.None, off.Problem());
        Assert.Equal(AppLoginProblem.InheritAuth, (off with { Request = off.Request with { Auth = AuthSpec.Inherit } }).Problem());
    }
}
