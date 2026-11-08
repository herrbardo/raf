namespace Raf.Domain.Requests;

/// <summary>Calcula el request efectivo de un Sample: Request padre + overrides.</summary>
public static class SampleMerger
{
    public static RequestSpec Merge(RequestSpec parent, SampleOverrides overrides) => parent with
    {
        Method = overrides.Method ?? parent.Method,
        Url = overrides.Url ?? parent.Url,
        Headers = MergeItems(parent.Headers, overrides.Headers, StringComparer.OrdinalIgnoreCase),
        QueryParams = MergeItems(parent.QueryParams, overrides.QueryParams, StringComparer.Ordinal),
        Body = overrides.Body ?? parent.Body,
        Auth = overrides.Auth ?? parent.Auth,
    };

    private static IReadOnlyList<KeyValueItem> MergeItems(
        IReadOnlyList<KeyValueItem> parent,
        IReadOnlyList<KeyValueOverride>? overrides,
        StringComparer keyComparer)
    {
        if (overrides is null || overrides.Count == 0) return parent;

        var result = parent.ToList();
        foreach (var o in overrides)
        {
            if (o.Remove)
            {
                result.RemoveAll(i => keyComparer.Equals(i.Key, o.Key));
                continue;
            }

            var matched = false;
            for (var i = 0; i < result.Count; i++)
            {
                if (!keyComparer.Equals(result[i].Key, o.Key)) continue;
                matched = true;
                result[i] = result[i] with
                {
                    Value = o.Value ?? result[i].Value,
                    Enabled = o.Enabled ?? result[i].Enabled,
                };
            }

            if (!matched) result.Add(new KeyValueItem(o.Key, o.Value ?? "", o.Enabled ?? true));
        }
        return result;
    }
}
