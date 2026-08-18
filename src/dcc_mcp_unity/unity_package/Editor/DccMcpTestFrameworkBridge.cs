using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DccMcp.Unity
{
    internal static class DccMcpTestFrameworkBridge
    {
        private const string TestRunnerApiTypeName =
            "UnityEditor.TestTools.TestRunner.Api.TestRunnerApi";
        private const string ExecutionSettingsTypeName =
            "UnityEditor.TestTools.TestRunner.Api.ExecutionSettings";
        private const string FilterTypeName = "UnityEditor.TestTools.TestRunner.Api.Filter";
        private const string TestModeTypeName = "UnityEditor.TestTools.TestRunner.Api.TestMode";
        private const string ResultsCallbackTypeName =
            "UnityEditor.TestTools.TestRunner.CommandLineTest.ResultsSavingCallbacks";
        private const string CallbackNamePrefix = "DCC-MCP Test Results ";

        internal static void ValidateContract()
        {
            var assembly = LoadTestRunnerAssembly();
            var apiType = RequireType(assembly, TestRunnerApiTypeName);
            var callbackType = RequireType(assembly, ResultsCallbackTypeName);
            var callbackHolderType = RequireType(
                assembly,
                "UnityEditor.TestTools.TestRunner.Api.CallbacksHolder");
            var settingsType = RequireType(assembly, ExecutionSettingsTypeName);
            var filterType = RequireType(assembly, FilterTypeName);
            RequireType(assembly, TestModeTypeName);

            if (!HasMember(callbackType, "m_ResultFilePath")
                || !HasMember(callbackHolderType, "m_Callbacks")
                || !HasMember(filterType, "testMode")
                || !HasMember(filterType, "testNames")
                || (!HasMember(settingsType, "filter") && !HasMember(settingsType, "filters")))
            {
                throw new InvalidOperationException(
                    "Unity Test Framework reflection members do not match the typed runner contract.");
            }
            var hasExecute = apiType.GetMethod(
                "Execute",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { settingsType },
                null) != null;
            var hasCallbacks = false;
            foreach (var method in apiType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                hasCallbacks |= method.Name == "RegisterCallbacks"
                    && method.GetParameters().Length == 2;
            }
            if (!hasExecute || !hasCallbacks)
            {
                throw new InvalidOperationException(
                    "Unity Test Framework execution methods do not match the typed runner contract.");
            }
        }

        internal static void Start(JObject parameters, string reportPath)
        {
            var assembly = LoadTestRunnerAssembly();
            var apiType = RequireType(assembly, TestRunnerApiTypeName);
            var callbackType = RequireType(assembly, ResultsCallbackTypeName);
            var executionSettingsType = RequireType(assembly, ExecutionSettingsTypeName);
            var filterType = RequireType(assembly, FilterTypeName);
            var testModeType = RequireType(assembly, TestModeTypeName);

            var api = CreateScriptableObject(apiType, "DCC-MCP Test Runner API");
            RegisterCallbackIfMissing(
                apiType,
                api,
                callbackType,
                (string)parameters["request_id"],
                reportPath);

            var filter = Activator.CreateInstance(filterType, true);
            SetRequiredMember(
                filter,
                "testMode",
                Enum.Parse(
                    testModeType,
                    (string)parameters["test_mode"] == "edit_mode" ? "EditMode" : "PlayMode"));
            SetRequiredMember(
                filter,
                "testNames",
                ((JArray)parameters["test_names"]).ToObject<string[]>());

            var settings = CreateExecutionSettings(executionSettingsType, filterType, filter);
            var execute = apiType.GetMethod(
                "Execute",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { executionSettingsType },
                null);
            if (execute == null)
            {
                throw new InvalidOperationException(
                    "Unity Test Framework does not expose the expected Execute contract.");
            }
            Invoke(execute, api, new[] { settings });
        }

        internal static void EnsureCallback(string requestId, string reportPath)
        {
            var assembly = LoadTestRunnerAssembly();
            var apiType = RequireType(assembly, TestRunnerApiTypeName);
            var callbackType = RequireType(assembly, ResultsCallbackTypeName);
            var api = CreateScriptableObject(apiType, "DCC-MCP Test Runner Callback API");
            RegisterCallbackIfMissing(apiType, api, callbackType, requestId, reportPath);
        }

        internal static bool IsCallbackRegistered(string requestId, string reportPath = null)
        {
            var assembly = LoadTestRunnerAssembly();
            var expectedName = CallbackNamePrefix + requestId;
            var callbackType = RequireType(assembly, ResultsCallbackTypeName);
            foreach (var entry in GetCallbacks(assembly))
            {
                var callback = GetCallback(entry);
                if (MatchesCallback(callback, callbackType, expectedName, reportPath))
                {
                    return true;
                }
            }
            return false;
        }

        internal static void ReleaseCallback(string requestId, string reportPath)
        {
            var assembly = LoadTestRunnerAssembly();
            var callbacks = GetCallbacks(assembly);
            var callbackType = RequireType(assembly, ResultsCallbackTypeName);
            var expectedName = CallbackNamePrefix + requestId;
            for (var index = callbacks.Count - 1; index >= 0; index--)
            {
                var callback = GetCallback(callbacks[index]);
                if (MatchesCallback(callback, callbackType, expectedName, reportPath))
                {
                    callbacks.RemoveAt(index);
                    UnityEngine.Object.DestroyImmediate(callback);
                }
            }
        }

        private static void RegisterCallbackIfMissing(
            Type apiType,
            object api,
            Type callbackType,
            string requestId,
            string reportPath)
        {
            var callbacks = GetCallbacks(LoadTestRunnerAssembly());
            var expectedName = CallbackNamePrefix + requestId;
            var foundExpected = false;
            for (var index = callbacks.Count - 1; index >= 0; index--)
            {
                var callback = GetCallback(callbacks[index]);
                if (MatchesCallback(callback, callbackType, expectedName, reportPath))
                {
                    if (!foundExpected)
                    {
                        foundExpected = true;
                        continue;
                    }
                }
                else if (!IsOwnedResultsCallback(callback, callbackType))
                {
                    continue;
                }

                callbacks.RemoveAt(index);
                UnityEngine.Object.DestroyImmediate(callback);
            }
            if (foundExpected)
            {
                return;
            }
            var registeredCallback = CreateScriptableObject(
                callbackType,
                CallbackNamePrefix + requestId);
            SetRequiredMember(registeredCallback, "m_ResultFilePath", reportPath);
            RegisterCallback(apiType, api, callbackType, registeredCallback);
        }

        private static bool MatchesCallback(
            UnityEngine.Object callback,
            Type callbackType,
            string expectedName,
            string reportPath)
        {
            if (callback == null || !callbackType.IsInstanceOfType(callback))
            {
                return false;
            }
            if (string.Equals(callback.name, expectedName, StringComparison.Ordinal))
            {
                return true;
            }
            return !string.IsNullOrEmpty(reportPath)
                && PathsEqual(ReadResultPath(callback), reportPath);
        }

        private static bool IsOwnedResultsCallback(UnityEngine.Object callback, Type callbackType)
        {
            if (callback == null || !callbackType.IsInstanceOfType(callback))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(callback.name)
                && callback.name.StartsWith(CallbackNamePrefix, StringComparison.Ordinal))
            {
                return true;
            }

            var reportPath = ReadResultPath(callback);
            if (string.IsNullOrEmpty(reportPath))
            {
                return false;
            }
            try
            {
                var projectPath = Path.GetFullPath(
                    Path.GetDirectoryName(Application.dataPath) ?? string.Empty);
                var reportsRoot = Path.GetFullPath(Path.Combine(
                    projectPath,
                    "Builds",
                    "DccMcp",
                    "Tests"));
                var fullPath = Path.GetFullPath(reportPath);
                var comparison = Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                return fullPath.StartsWith(
                    reportsRoot + Path.DirectorySeparatorChar,
                    comparison);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ReadResultPath(UnityEngine.Object callback)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var field = callback.GetType().GetField("m_ResultFilePath", flags);
            if (field != null)
            {
                return field.GetValue(callback) as string;
            }
            var property = callback.GetType().GetProperty("m_ResultFilePath", flags);
            return property == null ? null : property.GetValue(callback, null) as string;
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }
            var comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IList GetCallbacks(Assembly assembly)
        {
            var callbackHolderType = RequireType(
                assembly,
                "UnityEditor.TestTools.TestRunner.Api.CallbacksHolder");
            var instanceProperty = callbackHolderType.GetProperty(
                "instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                | BindingFlags.FlattenHierarchy);
            var holder = instanceProperty == null ? null : instanceProperty.GetValue(null, null);
            var callbacksField = callbackHolderType.GetField(
                "m_Callbacks",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var callbacks = callbacksField == null ? null : callbacksField.GetValue(holder) as IList;
            if (callbacks == null)
            {
                throw new InvalidOperationException(
                    "Unity Test Framework does not expose its callback registry.");
            }
            return callbacks;
        }

        private static UnityEngine.Object GetCallback(object entry)
        {
            if (entry == null)
            {
                return null;
            }
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var field = entry.GetType().GetField("Callback", flags);
            if (field != null)
            {
                return field.GetValue(entry) as UnityEngine.Object;
            }
            var property = entry.GetType().GetProperty("Callback", flags);
            return property == null ? null : property.GetValue(entry, null) as UnityEngine.Object;
        }

        private static Assembly LoadTestRunnerAssembly()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(
                    assembly.GetName().Name,
                    "UnityEditor.TestRunner",
                    StringComparison.Ordinal))
                {
                    return assembly;
                }
            }
            try
            {
                return Assembly.Load("UnityEditor.TestRunner");
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Unity Test Framework is unavailable; install or enable the Unity Test Runner package.",
                    exception);
            }
        }

        private static Type RequireType(Assembly assembly, string name)
        {
            var type = assembly.GetType(name, false);
            if (type == null)
            {
                throw new InvalidOperationException(
                    "Unity Test Framework is missing the required typed runner contract: " + name);
            }
            return type;
        }

        private static UnityEngine.Object CreateScriptableObject(Type type, string name)
        {
            var value = ScriptableObject.CreateInstance(type);
            if (value == null)
            {
                throw new InvalidOperationException(
                    "Unity Test Framework could not create " + type.FullName + ".");
            }
            value.name = name;
            return value;
        }

        private static object CreateExecutionSettings(Type settingsType, Type filterType, object filter)
        {
            var filters = Array.CreateInstance(filterType, 1);
            filters.SetValue(filter, 0);

            var ctor = settingsType.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { filters.GetType() }, null);

            if (ctor != null) return ctor.Invoke(new object[] { filters });

            var settings = Activator.CreateInstance(settingsType, true);
            if (TrySetMember(settings, "filter", filter))
            {
                return settings;
            }
            if (TrySetMember(settings, "filters", filters))
            {
                return settings;
            }
            throw new InvalidOperationException(
                "Unity Test Framework does not expose a compatible filter contract.");
        }

        private static void RegisterCallback(Type apiType, object api, Type callbackType, object callback)
        {
            foreach (var method in apiType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name != "RegisterCallbacks" || method.GetParameters().Length != 2)
                {
                    continue;
                }
                var callable = method.IsGenericMethodDefinition
                    ? method.MakeGenericMethod(callbackType)
                    : method;
                Invoke(callable, api, new[] { callback, (object)0 });
                return;
            }
            throw new InvalidOperationException(
                "Unity Test Framework does not expose the expected callback contract.");
        }

        private static void SetRequiredMember(object target, string name, object value)
        {
            if (!TrySetMember(target, name, value))
            {
                throw new InvalidOperationException(
                    "Unity Test Framework is missing required member " + name + ".");
            }
        }

        private static bool TrySetMember(object target, string name, object value)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var field = target.GetType().GetField(name, flags);
            if (field != null)
            {
                field.SetValue(target, value);
                return true;
            }
            var property = target.GetType().GetProperty(name, flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, value, null);
                return true;
            }
            return false;
        }

        private static bool HasMember(Type type, string name)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            return type.GetField(name, flags) != null || type.GetProperty(name, flags) != null;
        }

        private static object Invoke(MethodInfo method, object target, object[] arguments)
        {
            try
            {
                return method.Invoke(target, arguments);
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    "Unity Test Framework invocation failed: "
                    + (exception.InnerException?.Message ?? exception.Message),
                    exception.InnerException ?? exception);
            }
        }
    }
}
