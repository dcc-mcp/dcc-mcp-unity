using System;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine.SceneManagement;

namespace DccMcp.Unity
{
    internal static class DccMcpSceneIdentity
    {
        internal static string GetId(Scene scene)
        {
#if UNITY_6000_5_OR_NEWER
            return scene.handle.GetRawData().ToString(CultureInfo.InvariantCulture);
#else
            return scene.handle.ToString(CultureInfo.InvariantCulture);
#endif
        }

        internal static string Require(JObject parameters)
        {
            var token = parameters["scene_handle"];
            if (token == null || (token.Type != JTokenType.String && token.Type != JTokenType.Integer))
                throw new InvalidOperationException("scene_handle must be a decimal string or a legacy integer.");
            var text = token.Type == JTokenType.String
                ? (string)token : token.ToString(Newtonsoft.Json.Formatting.None);
            // Keep the old 32-bit JSON integer input, but never accept a rounded
            // JavaScript number as a new 64-bit scene identity.
            int legacy;
            if (token.Type == JTokenType.Integer &&
                !int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out legacy))
                throw new InvalidOperationException("Large scene handles must be sent as decimal strings.");
            if (string.IsNullOrEmpty(text) || text.Length > 20)
                throw new InvalidOperationException("Invalid scene handle.");
            for (var index = text[0] == '-' ? 1 : 0; index < text.Length; index++)
                if (text[index] < '0' || text[index] > '9')
                    throw new InvalidOperationException("Invalid scene handle.");
#if UNITY_6000_5_OR_NEWER
            ulong value;
            if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
#else
            int value;
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
#endif
                return value.ToString(CultureInfo.InvariantCulture);
            throw new InvalidOperationException("Scene handle is outside this Editor's supported range.");
        }
    }
}
