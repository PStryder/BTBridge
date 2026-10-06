using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace BTBridge.Tests
{
    /// <summary>
    /// HBS's LazySingletonBehavior&lt;T&gt;.Instance creates the singleton when the game hasn't yet.
    /// The overlay called it for UIManager from its first frame and the game hung on a black screen
    /// at launch. Only Ui/GameUi.cs may touch .Instance, and only behind HasInstance.
    /// </summary>
    public class SingletonGuardTests
    {
        // From this file's own path: xunit may run a shadow copy of the assembly elsewhere.
        private static string ModSourceDir([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        {
            var dir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here)), "BTBridge");
            Assert.True(Directory.Exists(Path.Combine(dir, "Ui")), "mod source not found at " + dir);
            return dir;
        }

        private static readonly Regex CreatingAccess = new Regex(@"LazySingletonBehavior<[^>]+>\s*\.\s*Instance\b");

        [Fact]
        public void No_source_file_but_GameUi_uses_the_creating_accessor()
        {
            var offenders = Directory.GetFiles(ModSourceDir(), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Where(f => Path.GetFileName(f) != "GameUi.cs")
                .Where(f => CreatingAccess.IsMatch(File.ReadAllText(f)))
                .Select(Path.GetFileName)
                .ToList();
            Assert.True(offenders.Count == 0,
                "LazySingletonBehavior<T>.Instance creates the singleton if missing; use Ui.GameUi.Existing(). In: "
                + string.Join(", ", offenders));
        }

        [Fact]
        public void GameUi_checks_HasInstance_before_Instance()
        {
            var text = File.ReadAllText(Path.Combine(ModSourceDir(), "Ui", "GameUi.cs"));
            // The one permitted read is the guarded conditional itself.
            var guarded = new Regex(@"LazySingletonBehavior<UIManager>\.HasInstance\s*\?\s*LazySingletonBehavior<UIManager>\.Instance\s*:\s*null");
            int reads = Regex.Matches(text, @"LazySingletonBehavior<UIManager>\.Instance\b").Count;
            Assert.True(guarded.IsMatch(text) && reads == 1,
                $"GameUi.Existing must test HasInstance before reading Instance (guarded={guarded.IsMatch(text)}, reads={reads})");
        }
    }
}
