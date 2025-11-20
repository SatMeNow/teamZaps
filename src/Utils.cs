using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace teamZaps.Utils
{
    public static partial class ExtEnum
    {
        public static string GetDescription(this Enum source) => GetFieldInfo(source)?.TryGetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        public static string GetIcon(this Enum source) => GetFieldInfo(source)?.TryGetCustomAttribute<IconAttribute>()?.Icon ?? "";
        private static FieldInfo? GetFieldInfo(this Enum source) => source.GetType().GetField(source.ToString());
        private static T? TryGetCustomAttribute<T>(this FieldInfo? source)
            where T : Attribute
        {
            var attr = (T[]?)source?.GetCustomAttributes(typeof(T), false);
            if (attr?.Length > 0)
                return (attr[0]);
            else
                return (null);
        }
    }
}