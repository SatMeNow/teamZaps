using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;

namespace teamZaps.Utils
{
    #region Utilities
    internal static partial class UtilAssembly
    {
        /// <summary>
        /// Determines defined types of the specified type.
        /// </summary>
        /// <remarks>
        /// The <typeparamref name="T"/> type decides how to determine types: By decorated <see cref="Attribute">attributes</see> or by <see cref="Type">base type</see>.
        /// </remarks>
        /// <typeparam name="T">Conditional type.</typeparam>
        /// <param name="assembly">Assembly to query.</param>
        /// <returns>Enumeration of matching types.</returns>
        public static IEnumerable<Type> GetDefinedTypesOf<T>(Assembly? assembly = null)
        {
            if (assembly is null)
                assembly = Assembly.GetExecutingAssembly();

            var type = typeof(T);
            if (typeof(Attribute).IsAssignableFrom(type))
                return (assembly.DefinedTypes.Where(t => t.UnderlyingSystemType.TryGetCustomAttribute(type) is not null));
            else
                return (assembly.DefinedTypes.Where(t => type.IsAssignableFrom(t.UnderlyingSystemType)));
        }
        /// <summary>
        /// Determines defined types that declare an attrute of the specified type to create a (Type|Attribute) map from.
        /// </summary>
        /// <inheritdoc cref="GetDefinedTypesOf"/>
        /// <returns>Dictionary of matching types, grouped per type.</returns>
        public static Dictionary<Type, TAttr> GetDefinedTypeMap<TAttr>(Assembly? assembly = null)
            where TAttr : Attribute
        {
            return (GetDefinedTypesOf<TAttr>(assembly).ToDictionary(
                t => t,
                t => t.GetCustomAttribute<TAttr>()!
            ));
        }
    }
    internal static partial class UtilEnum
    {
        /// <summary>
        /// Creates a mapping dictionary of enumerated values that are decorated with a certain attribute.
        /// </summary>
        /// <typeparam name="TEnum">Enumerated type.</typeparam>
        /// <typeparam name="TAttrib">Attribute type.</typeparam>
        public static IReadOnlyDictionary<TEnum, TAttrib> GetCustomAttributes<TEnum, TAttrib>(bool bInherit = false)
            where TEnum : struct, Enum
            where TAttrib : Attribute
        {
            return (GetCustomAttributes<TEnum, TAttrib, TEnum, TAttrib>(
                (_tEnum, _tAttrib) => _tEnum,
                (_tEnum, _tAttrib) => _tAttrib,
                bInherit
            ));
        }
        /// <inheritdoc cref="GetCustomAttributes"/>
        /// <param name="fKeySelector">Selector to obtain key.</param>
        /// <param name="fValueSelector">Selector to obtain value.</param>
        public static IReadOnlyDictionary<TKey, TVal> GetCustomAttributes<TEnum, TAttrib, TKey, TVal>(Func<TEnum, TAttrib, TKey> fKeySelector, Func<TEnum, TAttrib, TVal> fValueSelector, bool bInherit = false)
            where TEnum : struct, Enum
            where TAttrib : Attribute
        {
            var dResult = new Dictionary<TKey, TVal>();
            foreach (var _eVal in Enum.GetValues<TEnum>())
            {
                if (_eVal.TryGetCustomAttribute<TAttrib, TEnum>(out var _tAttrib, bInherit))
                    dResult.Add(fKeySelector(_eVal, _tAttrib), fValueSelector(_eVal, _tAttrib));
            }
            return (dResult);
        }
    }
    #endregion
    #region Extensions
    internal static partial class ExtType
    {
        public static Attribute? TryGetCustomAttribute(this Type source, Type attributeType, bool inherit = false)
        {
            var attrib = source.GetCustomAttributes(attributeType, inherit);
            if ((attrib != null) && (attrib.Count() == 1))
                return ((Attribute)attrib.First());
            else
                return (null);
        }
    }
    internal static class ExtFieldInfo
    {
        public static bool TryGetAttribute<T>(this FieldInfo field, out T attribute, bool inherit = false)
            where T : Attribute
        {
            var _oAttrib = field.GetCustomAttributes(typeof(T), inherit);
            if (_oAttrib.Length == 1)
                attribute = (T)_oAttrib[0];
            else
                attribute = null;
            return (attribute != null);
        }
        public static T GetAttribute<T>(this FieldInfo field, bool inherit = false)
            where T : Attribute
        {
            if (TryGetAttribute(field, out T tResult, inherit))
                return (tResult);
            else
                throw new KeyNotFoundException(string.Format("Did not found attribute of type '{0}'!", typeof(T).Name));
        }
    }
    internal static partial class ExtEnum
    {
        public static string GetDescription(this Enum source) => GetFieldInfo(source)?.TryGetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
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
        public static bool TryGetCustomAttribute<TAttrib, TEnum>(this TEnum value, out TAttrib attribute, bool bInherit = false)
            where TAttrib : Attribute
            where TEnum : struct, Enum
        {
            return (value.GetField().TryGetAttribute(out attribute, bInherit));
        }
        public static TAttrib GetCustomAttribute<TAttrib, TEnum>(this TEnum value, bool bInherit = false)
            where TAttrib : Attribute
            where TEnum : struct, Enum
        {
            return (value.GetField().GetAttribute<TAttrib>(bInherit));
        }
        public static FieldInfo GetField<T>(this T value)
            where T : struct, Enum
        {
            return (GetField((Enum)value));
        }
        public static FieldInfo GetField(this Enum eValue)
        {
            return (eValue.GetType().GetField(eValue.ToString())!);
        }
    }
    internal static partial class ExtEnumerable
    {
        public static bool IsEmpty<T>(this IEnumerable<T> source)
        {
            return ((source is null) || (!source.Any()));
        }
    }
    internal static partial class ExtDictionary
    {
        public static bool TryGetKeyOf<T, U>(this IEnumerable<KeyValuePair<T, U>> source, U value, [NotNullWhen(true)] out T? key) => source.TryGetKeyOf(v => (v?.Equals(value) == true), out key);
        public static bool TryGetKeyOf<T, U>(this IEnumerable<KeyValuePair<T, U>> source, Predicate<U> valueSelector, [NotNullWhen(true)] out T? key)
        {
            foreach (var item in source)
            {
                if (valueSelector(item.Value))
                {
                    key = item.Key!;
                    return (true);
                }
            }
            key = default;
            return (false);
        }
        public static T GetKeyOf<T, U>(this IEnumerable<KeyValuePair<T, U>> source, U value)
        {
            if (source.TryGetKeyOf(value, out T key))
                return (key);
            else
                throw new NotImplementedException($"Failed to obtain key for missing value '{value}'!");
        }
        public static T GetKeyOf<T, U>(this IEnumerable<KeyValuePair<T, U>> source, Predicate<U> valueSelector)
        {
            if (source.TryGetKeyOf(valueSelector, out T key))
                return (key);
            else
                throw new NotImplementedException($"Failed to obtain key due to conditional missmatch!");
        }
    }
    internal static partial class ExtRegEx
    {
        public static string? TryGetValue(this GroupCollection source, string key)
        {
            if ((source.TryGetValue(key, out Group? group)) && (group.Success))
                return (group!.Value);
            else
                return (null);
        }
    }
    #endregion
}