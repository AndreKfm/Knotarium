namespace KnotGarden.Core.Domain;

public readonly struct SecretValue
{
    private readonly string _value;

    public SecretValue(string? value)
    {
        _value = value ?? string.Empty;
    }

    public bool HasValue => !string.IsNullOrEmpty(_value);

    public string Reveal() => _value;

    public override string ToString() => "***";

    public static SecretValue Empty => new(string.Empty);
}