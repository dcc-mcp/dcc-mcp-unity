using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace DccMcp.Unity
{
    internal static class DccMcpObjectIdentity
    {
        internal static string GetId(Object value)
        {
#if UNITY_6000_5_OR_NEWER
            return EntityId.ToULong(value.GetEntityId()).ToString(CultureInfo.InvariantCulture);
#else
            return value.GetInstanceID().ToString(CultureInfo.InvariantCulture);
#endif
        }

        internal static bool TryNormalize(string id, out string normalized)
        {
#if UNITY_6000_5_OR_NEWER
            ulong value;
            if (ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                normalized = value.ToString(CultureInfo.InvariantCulture);
                return true;
            }
#else
            int value;
            if (int.TryParse(
                id,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out value))
            {
                normalized = value.ToString(CultureInfo.InvariantCulture);
                return true;
            }
#endif
            normalized = null;
            return false;
        }

        internal static Object Resolve(string id)
        {
            string normalized;
            if (!TryNormalize(id, out normalized))
            {
                return null;
            }
#if UNITY_6000_5_OR_NEWER
            var value = ulong.Parse(normalized, CultureInfo.InvariantCulture);
            return EditorUtility.EntityIdToObject(EntityId.FromULong(value));
#else
            var value = int.Parse(normalized, CultureInfo.InvariantCulture);
            return EditorUtility.InstanceIDToObject(value);
#endif
        }
    }
}
