using Raf.Domain.Requests;

namespace Raf.Client.Core.Execution;

public static class AppLoginTexts
{
    /// <summary>Qué le falta a un login de App para guardarse, en el idioma de la interfaz.</summary>
    public static string Describe(AppLoginProblem problem) => problem switch
    {
        AppLoginProblem.Incomplete => CoreTexts.LoginProblem_Incomplete,
        AppLoginProblem.InheritAuth => CoreTexts.LoginProblem_InheritAuth,
        AppLoginProblem.InvalidMethod => CoreTexts.LoginProblem_InvalidMethod,
        AppLoginProblem.MissingUrl => CoreTexts.LoginProblem_MissingUrl,
        AppLoginProblem.MissingTokenPath => CoreTexts.LoginProblem_MissingTokenPath,
        AppLoginProblem.InvalidVariable => CoreTexts.LoginProblem_InvalidVariable,
        AppLoginProblem.MissingExpiryPath => CoreTexts.LoginProblem_MissingExpiryPath,
        AppLoginProblem.InvalidExpiryMinutes => CoreTexts.LoginProblem_InvalidExpiryMinutes,
        _ => "",
    };
}
