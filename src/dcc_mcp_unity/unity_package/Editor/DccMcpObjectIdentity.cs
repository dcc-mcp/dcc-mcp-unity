using UnityEditor;
using UnityEngine;

namespace DccMcp.Unity
{
    internal static class DccMcpObjectIdentity
    {
        internal static int GetId(Object value)
        {
#if UNITY_6000_5_OR_NEWER
            return value.GetEntityId();
#else
            return value.GetInstanceID();
#endif
        }

        internal static Object Resolve(int id)
        {
#if UNITY_6000_5_OR_NEWER
            return EditorUtility.EntityIdToObject((EntityId)id);
#else
            return EditorUtility.InstanceIDToObject(id);
#endif
        }
    }
}
