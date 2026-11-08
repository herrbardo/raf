using Raf.Domain.Requests;

namespace Raf.Domain.Tests;

public class SampleMergerTests
{
    private static readonly RequestSpec Parent = new(
        "GET",
        "https://api.test/users",
        [new("Accept", "application/json"), new("X-Trace", "1")],
        [new("page", "1"), new("size", "20")],
        new BodySpec(BodyType.Json, """{"a":1}"""),
        new AuthSpec(AuthType.Bearer, Token: "{{token}}"));

    [Fact]
    public void Empty_overrides_inherit_everything()
    {
        var merged = SampleMerger.Merge(Parent, SampleOverrides.Empty);

        Assert.Equal(Parent.Method, merged.Method);
        Assert.Equal(Parent.Url, merged.Url);
        Assert.Equal(Parent.Headers, merged.Headers);
        Assert.Equal(Parent.Body, merged.Body);
        Assert.Equal(Parent.Auth, merged.Auth);
    }

    [Fact]
    public void Scalars_are_replaced_when_defined()
    {
        var merged = SampleMerger.Merge(Parent, new SampleOverrides(
            Method: "POST",
            Body: new BodySpec(BodyType.Json, """{"b":2}"""),
            Auth: AuthSpec.None));

        Assert.Equal("POST", merged.Method);
        Assert.Equal(Parent.Url, merged.Url);
        Assert.Equal("""{"b":2}""", merged.Body.Content);
        Assert.Equal(AuthType.None, merged.Auth.Type);
    }

    [Fact]
    public void Headers_are_replaced_added_and_removed_by_key_case_insensitive()
    {
        var merged = SampleMerger.Merge(Parent, new SampleOverrides(Headers:
        [
            new("accept", "text/plain"),
            new("x-trace", Remove: true),
            new("X-New", "yes"),
        ]));

        Assert.Equal(
            [new KeyValueItem("Accept", "text/plain"), new KeyValueItem("X-New", "yes")],
            merged.Headers);
    }

    [Fact]
    public void Override_can_disable_an_inherited_item_keeping_its_value()
    {
        var merged = SampleMerger.Merge(Parent, new SampleOverrides(QueryParams: [new("size", Enabled: false)]));

        Assert.Equal(
            [new KeyValueItem("page", "1"), new KeyValueItem("size", "20", Enabled: false)],
            merged.QueryParams);
    }

    [Fact]
    public void Query_param_keys_are_case_sensitive()
    {
        var merged = SampleMerger.Merge(Parent, new SampleOverrides(QueryParams: [new("PAGE", "9")]));

        Assert.Equal(3, merged.QueryParams.Count);
        Assert.Equal("1", merged.QueryParams[0].Value);
    }

    [Fact]
    public void Parent_changes_flow_to_sample_where_not_overridden()
    {
        var sample = new SampleOverrides(Method: "DELETE");
        var updatedParent = Parent with { Url = "https://api.test/v2/users" };

        var merged = SampleMerger.Merge(updatedParent, sample);

        Assert.Equal("DELETE", merged.Method);
        Assert.Equal("https://api.test/v2/users", merged.Url);
    }
}
