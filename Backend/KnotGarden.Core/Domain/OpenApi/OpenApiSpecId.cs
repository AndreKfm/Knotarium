namespace KnotGarden.Core.Domain.OpenApi;

public readonly record struct OpenApiSpecId(string Value)
{
    public override string ToString() => Value;
}
