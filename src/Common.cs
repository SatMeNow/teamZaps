namespace teamZaps;

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class IconAttribute : Attribute
{
    public IconAttribute(string icon)
    {
        this.Icon = icon;
    }

    public string Icon { get; }
}
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class CurrencyAttribute : Attribute, IEquatable<string>
{
    public CurrencyAttribute(string Symbol, string unitName, string[] abbreviations)
    {
        this.Symbol = Symbol;
        this.UnitName = unitName;
        this.Abbreviations = abbreviations;
    }

    public string Symbol { get; }
    public string UnitName { get; }
    public string[] Abbreviations { get; }

    public bool Equals(string? other)
    {
        if (Symbol.Equals(other))
            return (true);
        if (Abbreviations.Contains(other?.ToLowerInvariant()))
            return (true);

        return (false);
    }
}