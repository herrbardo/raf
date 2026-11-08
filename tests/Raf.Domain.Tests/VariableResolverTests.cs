using Raf.Domain.Requests;

namespace Raf.Domain.Tests;

public class VariableResolverTests
{
    [Fact]
    public void Environment_overrides_workspace_globals()
    {
        var resolver = VariableResolver.FromLayers(
            [new("host", "global.test"), new("version", "v1")],
            [new("host", "dev.test")]);

        Assert.Equal("https://dev.test/v1", resolver.Resolve("https://{{host}}/{{ version }}"));
    }

    [Fact]
    public void Unknown_variables_are_kept_and_reported()
    {
        var resolver = new VariableResolver(new Dictionary<string, string>());

        Assert.Equal("{{missing}}/x", resolver.Resolve("{{missing}}/x"));
        Assert.Equal(["missing"], resolver.Missing);
    }

    [Fact]
    public void Variables_can_reference_other_variables()
    {
        var resolver = new VariableResolver(new Dictionary<string, string>
        {
            ["baseUrl"] = "https://{{host}}/api",
            ["host"] = "prod.test",
        });

        Assert.Equal("https://prod.test/api/users", resolver.Resolve("{{baseUrl}}/users"));
    }

    [Fact]
    public void Self_reference_does_not_loop_forever()
    {
        var resolver = new VariableResolver(new Dictionary<string, string> { ["a"] = "x{{a}}" });

        var result = resolver.Resolve("{{a}}");

        Assert.StartsWith("xxx", result);
    }

    [Fact]
    public void Resolves_every_part_of_a_request()
    {
        var resolver = new VariableResolver(new Dictionary<string, string>
        {
            ["host"] = "h", ["tok"] = "T", ["id"] = "42", ["user"] = "u",
        });
        var spec = new RequestSpec(
            "GET",
            "https://{{host}}/x",
            [new("X-Id", "{{id}}")],
            [new("q", "{{id}}")],
            new BodySpec(BodyType.Json, """{"id":"{{id}}"}"""),
            new AuthSpec(AuthType.Bearer, Token: "{{tok}}", Username: "{{user}}"));

        var r = resolver.Resolve(spec);

        Assert.Equal("https://h/x", r.Url);
        Assert.Equal("42", r.Headers[0].Value);
        Assert.Equal("42", r.QueryParams[0].Value);
        Assert.Equal("""{"id":"42"}""", r.Body.Content);
        Assert.Equal("T", r.Auth.Token);
        Assert.Equal("u", r.Auth.Username);
        Assert.Empty(resolver.Missing);
    }
}
