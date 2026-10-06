using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Harmony;
using Xunit;

namespace BTBridge.Tests
{
    /// <summary>
    /// Every [HarmonyPatch] must resolve to exactly one method in the installed game. One that
    /// doesn't throws inside PatchAll and the whole mod fails to start: StartConversation has two
    /// overloads, and naming it without argument types took the bridge down at game launch.
    /// </summary>
    public class PatchTargetTests
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        static PatchTargetTests()
        {
            // Game assemblies the test project doesn't copy (ShadowrunDTO, Unity modules...).
            var managed = Path.GetDirectoryName(typeof(BattleTech.SimGameState).Assembly.Location);
            var gameManaged = Path.Combine(
                Environment.GetEnvironmentVariable("BT_GAME_DIR") ?? @"F:\SteamLibrary\steamapps\common\BATTLETECH",
                "BattleTech_Data", "Managed");
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var file = new AssemblyName(e.Name).Name + ".dll";
                foreach (var dir in new[] { managed, gameManaged })
                {
                    var p = Path.Combine(dir, file);
                    if (File.Exists(p))
                    {
                        return Assembly.LoadFrom(p);
                    }
                }
                return null;
            };
        }

        public static IEnumerable<object[]> Patches()
        {
            foreach (var t in typeof(BTBridge.Main).Assembly.GetTypes())
            {
                var attrs = t.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>().ToList();
                if (attrs.Count > 0)
                {
                    yield return new object[] { t.FullName };
                }
            }
        }

        /// <summary>Merge the class's [HarmonyPatch] attributes the way PatchAll does.</summary>
        private static HarmonyMethod Target(Type patchClass)
        {
            var merged = new HarmonyMethod();
            foreach (HarmonyPatch a in patchClass.GetCustomAttributes(typeof(HarmonyPatch), false))
            {
                var info = a.info;
                merged.declaringType = info.declaringType ?? merged.declaringType;
                merged.methodName = info.methodName ?? merged.methodName;
                merged.argumentTypes = info.argumentTypes ?? merged.argumentTypes;
            }
            return merged;
        }

        [Theory]
        [MemberData(nameof(Patches))]
        public void Patch_target_resolves_to_exactly_one_method(string patchClass)
        {
            var target = Target(typeof(BTBridge.Main).Assembly.GetType(patchClass));
            Assert.NotNull(target.declaringType);
            Assert.NotNull(target.methodName);
            var candidates = target.declaringType.GetMethods(All).Where(m => m.Name == target.methodName).ToList();
            if (target.argumentTypes != null)
            {
                candidates = candidates
                    .Where(m => m.GetParameters().Select(p => p.ParameterType).SequenceEqual(target.argumentTypes))
                    .ToList();
            }
            Assert.True(candidates.Count == 1,
                $"{patchClass}: {target.declaringType.Name}.{target.methodName} matches {candidates.Count} methods; " +
                "PatchAll would throw and the mod would not start. Name the argument types.");
        }

        /// <summary>
        /// Harmony binds patch parameters by name: one that names no parameter of the original (and
        /// isn't __instance/__result/__state/___field) fails at patch time and the mod doesn't start.
        /// </summary>
        [Theory]
        [MemberData(nameof(Patches))]
        public void Patch_parameters_name_real_parameters(string patchClass)
        {
            var type = typeof(BTBridge.Main).Assembly.GetType(patchClass);
            var target = Target(type);
            var original = target.declaringType.GetMethods(All)
                .Where(m => m.Name == target.methodName)
                .Where(m => target.argumentTypes == null
                    || m.GetParameters().Select(p => p.ParameterType).SequenceEqual(target.argumentTypes))
                .Single();
            var names = new HashSet<string>(original.GetParameters().Select(p => p.Name));
            foreach (var patch in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                         .Where(m => m.Name == "Prefix" || m.Name == "Postfix"))
            {
                foreach (var p in patch.GetParameters())
                {
                    bool special = p.Name == "__instance" || p.Name == "__result" || p.Name == "__state"
                        || p.Name.StartsWith("___") || p.Name == "__originalMethod" || p.Name == "__args";
                    Assert.True(special || names.Contains(p.Name),
                        $"{patchClass}.{patch.Name}: parameter '{p.Name}' is not a parameter of " +
                        $"{target.declaringType.Name}.{target.methodName} ({string.Join(", ", names)})");
                }
            }
        }

        [Fact]
        public void The_check_catches_an_ambiguous_target()
        {
            var both = typeof(BattleTech.SimGameConversationManager).GetMethods(All)
                .Count(m => m.Name == "StartConversation");
            Assert.Equal(2, both);
        }
    }
}
