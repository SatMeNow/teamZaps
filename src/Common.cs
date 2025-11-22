namespace teamZaps;

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class IconAttribute : Attribute
{
    public IconAttribute(string icon)
    {
        Icon = icon;
    }
    public string Icon { get; }
}