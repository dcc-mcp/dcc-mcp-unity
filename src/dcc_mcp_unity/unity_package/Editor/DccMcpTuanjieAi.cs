using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DccMcp.Unity
{
    internal static class DccMcpTuanjieAi
    {
        private const string BridgeTypeName = "UnityTcp.Editor.Tools.ExecuteCustomTool";
        private const string ToolAssemblyName = "UnityTcp.CustomTool";

        internal static JObject Inspect()
        {
            var bridgeType = FindBridgeType();
            var toolAssembly = FindToolAssembly();
            var toolNames = DiscoverRegisteredToolNames(bridgeType);
            return new JObject
            {
                ["available"] = bridgeType != null && toolNames.Length > 0,
                ["bridge_available"] = bridgeType != null,
                ["generator_package_loaded"] = toolAssembly != null,
                ["tools"] = new JArray(toolNames),
            };
        }

        internal static JObject Execute(JObject parameters)
        {
            var toolName = (string)(parameters ?? new JObject())["tool_name"];
            if (string.IsNullOrWhiteSpace(toolName))
            {
                throw new InvalidOperationException("tool_name is required.");
            }

            var toolParameters = parameters["parameters"];
            if (toolParameters != null
                && toolParameters.Type != JTokenType.Null
                && toolParameters.Type != JTokenType.Object)
            {
                throw new InvalidOperationException("parameters must be an object.");
            }

            var bridgeType = FindBridgeType();
            if (bridgeType == null)
            {
                throw new InvalidOperationException(
                    "Tuanjie AI custom tools are unavailable; install and load "
                    + "cn.tuanjie.codely.bridge and at least one CustomTool provider.");
            }

            var availableTools = DiscoverRegisteredToolNames(bridgeType);
            if (!availableTools.Contains(toolName, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "Tuanjie AI custom tool is unavailable: " + toolName);
            }

            return InvokeNative(
                bridgeType,
                new JObject
                {
                    ["tool_name"] = toolName,
                    ["parameters"] = toolParameters?.DeepClone() ?? new JObject(),
                });
        }

        private static JObject InvokeNative(Type bridgeType, JObject request)
        {
            var handle = bridgeType.GetMethod(
                "HandleCommand",
                BindingFlags.Public | BindingFlags.Static);
            if (handle == null || handle.GetParameters().Length != 1)
            {
                throw new InvalidOperationException(
                    "Tuanjie Codely bridge does not expose ExecuteCustomTool.HandleCommand.");
            }

            var nativeObjectType = handle.GetParameters()[0].ParameterType;
            var parse = nativeObjectType.GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
            if (parse == null)
            {
                throw new InvalidOperationException("Tuanjie Codely JSON parser is unavailable.");
            }

            try
            {
                var nativeRequest = parse.Invoke(
                    null,
                    new object[] { request.ToString(Formatting.None) });
                var nativeResult = handle.Invoke(null, new[] { nativeRequest });
                return ConvertResult(nativeObjectType.Assembly, nativeResult);
            }
            catch (TargetInvocationException exception)
            {
                var cause = exception.InnerException ?? exception;
                throw new InvalidOperationException(
                    "Tuanjie AI custom tool failed: " + cause.Message,
                    cause);
            }
        }

        private static JObject ConvertResult(Assembly jsonAssembly, object nativeResult)
        {
            var jsonConvert = jsonAssembly.GetType("Codely.Newtonsoft.Json.JsonConvert", false);
            var serialize = jsonConvert?
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                    method.Name == "SerializeObject" && method.GetParameters().Length == 1);
            if (serialize == null)
            {
                throw new InvalidOperationException("Tuanjie Codely JSON serializer is unavailable.");
            }

            var json = (string)serialize.Invoke(null, new[] { nativeResult });
            var token = JToken.Parse(json);
            return token as JObject ?? new JObject { ["value"] = token };
        }

        private static Type FindBridgeType()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(BridgeTypeName, false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        private static Assembly FindToolAssembly()
        {
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                assembly => assembly.GetName().Name == ToolAssemblyName);
        }

        internal static string[] DiscoverRegisteredToolNames(Type bridgeType)
        {
            var getRegisteredTools = bridgeType?.GetMethod(
                "GetRegisteredTools",
                BindingFlags.Public | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);
            if (getRegisteredTools == null)
            {
                return new string[0];
            }

            var names = getRegisteredTools.Invoke(null, null) as IEnumerable<string>;
            return names == null
                ? new string[0]
                : names
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
        }
    }
}
