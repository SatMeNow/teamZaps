using System.ComponentModel;
using System.Reflection;

namespace teamZaps.Utils
{
    public static partial class ExtEnum
    {
        public static FieldInfo? GetFieldInfo(this Enum source) => source.GetType().GetField(source.ToString());
        public static string GetDescription(this Enum source) => GetDescription(GetFieldInfo(source));
        public static string GetDescription(this FieldInfo? source)
        {
            if (source is null)
                return (string.Empty);

            var attr = (DescriptionAttribute[])source.GetCustomAttributes(typeof(DescriptionAttribute), false);
            if ((attr != null) && (attr.Length > 0))
                return (attr[0].Description);
            else
                return (source.Name);
        }
    }
}