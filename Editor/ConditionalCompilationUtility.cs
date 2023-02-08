﻿#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using Assembly = System.Reflection.Assembly;
using Debug = UnityEngine.Debug;
using System.Threading;

namespace ConditionalCompilation
{
    /// <summary>
    /// The Conditional Compilation Utility (CCU) will add defines to the build settings once dependendent classes have been detected.
    /// A goal of the CCU was to not require the CCU itself for other libraries to specify optional dependencies. So, it relies on the
    /// specification of at least one custom attribute in a project that makes use of it. Here is an example:
    ///
    /// [Conditional(UNITY_CCU)]                                    // | This is necessary for CCU to pick up the right attributes
    /// public class OptionalDependencyAttribute : Attribute        // | Must derive from System.Attribute
    /// {
    ///     public string dependentClass;                           // | Required field specifying the fully qualified dependent class
    ///     public string define;                                   // | Required field specifying the define to add
    /// }
    ///
    /// Then, simply specify the assembly attribute(s) you created in any of your C# files:
    /// [assembly: OptionalDependency("UnityEngine.InputNew.InputSystem", "USE_NEW_INPUT")]
    /// [assembly: OptionalDependency("Valve.VR.IVRSystem", "ENABLE_STEAMVR_INPUT")]
    ///
    /// namespace Foo
    /// {
    /// ...
    /// }
    /// </summary>
    [InitializeOnLoad]
    static class ConditionalCompilationUtility
    {
        const string k_PreviousUnsuccessfulDefines = "ConditionalCompilationUtility.PreviousUnsuccessfulDefines";
        const string k_EnableCCU = "UNITY_CCU";

        public static bool enabled
        {
            get
            {
                var buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
                return PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup).Contains(k_EnableCCU);
            }
        }

        public static string[] defines { private set; get; }

        static ConditionalCompilationUtility()
        {
#if UNITY_2017_3_OR_NEWER
            var errorsFound = false;
            CompilationPipeline.assemblyCompilationFinished += (outputPath, compilerMessages) =>
            {
                var errorCount = compilerMessages.Count(m => m.type == CompilerMessageType.Error && m.message.Contains("CS0246"));
                if (errorCount > 0 && !errorsFound)
                {
                    var previousDefines = EditorPrefs.GetString(k_PreviousUnsuccessfulDefines);
                    var currentDefines = string.Join(";", defines);
                    if (currentDefines != previousDefines)
                    {
                        // Store the last set of unsuccessful defines to avoid ping-ponging
                        EditorPrefs.SetString(k_PreviousUnsuccessfulDefines, currentDefines);

                        // Since there were errors in compilation, try removing any dependency defines
                        UpdateDependencies(true);
                    }
                    errorsFound = true;
                }
            };

            AssemblyReloadEvents.afterAssemblyReload += () =>
            {
                if (!errorsFound)
                    UpdateDependencies();
            };
#else
            UpdateDependencies();
#endif
        }

        static void UpdateDependencies(bool reset = false)
        {
            var buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (buildTargetGroup == BuildTargetGroup.Unknown)
            {
                var propertyInfo = typeof(EditorUserBuildSettings).GetProperty("activeBuildTargetGroup", BindingFlags.Static | BindingFlags.NonPublic);
                if (propertyInfo != null)
                    buildTargetGroup = (BuildTargetGroup)propertyInfo.GetValue(null, null);
            }

            var previousProjectDefines = PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup);
            var projectDefines = previousProjectDefines.Split(';').ToList();
            if (!projectDefines.Contains(k_EnableCCU, StringComparer.OrdinalIgnoreCase))
            {
                EditorApplication.LockReloadAssemblies();

                projectDefines.Add(k_EnableCCU);

                // This will trigger another re-compile, which needs to happen, so all the custom attributes will be visible
                PlayerSettings.SetScriptingDefineSymbolsForGroup(buildTargetGroup, string.Join(";", projectDefines.ToArray()));

                // Let other systems execute before reloading assemblies
                Thread.Sleep(1000);
                EditorApplication.UnlockReloadAssemblies();

                return;
            }

            var ccuDefines = new List<string> { k_EnableCCU };

            var conditionalAttributeType = typeof(ConditionalAttribute);

            const string kDependentClass = "dependentClass";
            const string kDefine = "define";

            var attributeTypes = GetAssignableTypes(typeof(Attribute), type =>
            {
                var conditionals = (ConditionalAttribute[])type.GetCustomAttributes(conditionalAttributeType, true);

                foreach (var conditional in conditionals)
                {
                    if (string.Equals(conditional.ConditionString, k_EnableCCU, StringComparison.OrdinalIgnoreCase))
                    {
                        var dependentClassField = type.GetField(kDependentClass);
                        if (dependentClassField == null)
                        {
                            Debug.LogErrorFormat("[CCU] Attribute type {0} missing field: {1}", type.Name, kDependentClass);
                            return false;
                        }

                        var defineField = type.GetField(kDefine);
                        if (defineField == null)
                        {
                            Debug.LogErrorFormat("[CCU] Attribute type {0} missing field: {1}", type.Name, kDefine);
                            return false;
                        }

                        return true;
                    }
                }

                return false;
            });

            var dependencies = new Dictionary<string, string>();
            ForEachAssembly(assembly =>
            {
                var typeAttributes = assembly.GetCustomAttributes(false).Cast<Attribute>();
                foreach (var typeAttribute in typeAttributes)
                {
                    if (attributeTypes.Contains(typeAttribute.GetType()))
                    {
                        var t = typeAttribute.GetType();

                        // These fields were already validated in a previous step
                        var dependentClass = t.GetField(kDependentClass).GetValue(typeAttribute) as string;
                        var define = t.GetField(kDefine).GetValue(typeAttribute) as string;

                        if (!string.IsNullOrEmpty(dependentClass) && !string.IsNullOrEmpty(define) && !dependencies.ContainsKey(dependentClass))
                            dependencies.Add(dependentClass, define);
                    }
                }
            });


            ForEachAssembly(assembly =>
            {
                foreach (var dependency in dependencies)
                {
                    var typeName = dependency.Key;
                    var define = dependency.Value;

                    var type = assembly.GetType(typeName);
                    if (type != null)
                    {
                        if (!projectDefines.Contains(define, StringComparer.OrdinalIgnoreCase))
                            projectDefines.Add(define);

                        ccuDefines.Add(define);
                    }
                }
            });

            // Remove scripting symbols for optional dependencies that have been removed.
            foreach (var define in dependencies.Values)
            {
                if (projectDefines.Contains(define) && !ccuDefines.Contains(define))
                    projectDefines.Remove(define);
            }

            if (reset)
            {
                foreach (var define in dependencies.Values)
                {
                    projectDefines.Remove(define);
                }

                ccuDefines.Clear();
                ccuDefines.Add(k_EnableCCU);
            }

            ConditionalCompilationUtility.defines = ccuDefines.ToArray();

            var newDefines = string.Join(";", projectDefines.ToArray());
            if (previousProjectDefines != newDefines)
                PlayerSettings.SetScriptingDefineSymbolsForGroup(buildTargetGroup, newDefines);
        }

        static void ForEachAssembly(Action<Assembly> callback)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                try
                {
                    callback(assembly);
                }
                catch (ReflectionTypeLoadException)
                {
                    // Skip any assemblies that don't load properly
                    continue;
                }
            }
        }

        static void ForEachType(Action<Type> callback)
        {
            ForEachAssembly(assembly =>
            {
                var types = assembly.GetTypes();
                foreach (var t in types)
                    callback(t);
            });
        }

        static IEnumerable<Type> GetAssignableTypes(Type type, Func<Type, bool> predicate = null)
        {
            var list = new List<Type>();
            ForEachType(t =>
            {
                if (type.IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract && (predicate == null || predicate(t)))
                    list.Add(t);
            });

            return list;
        }
    }
}
#endif
