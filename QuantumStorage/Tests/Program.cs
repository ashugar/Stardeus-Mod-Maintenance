using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

// This process invokes only the optional guard and presentation-only row cache.
// It never installs Harmony patches or invokes a Core/Unity gameplay callback.
static partial class Program
{
    static string Managed;
    internal static Assembly ModAssembly;
    static int checks;

    static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: MaterialPanelTests <Stardeus Managed directory> <QuantumStorage.dll>");
            return 2;
        }
        try
        {
            Managed = Path.GetFullPath(args[0]);
            string assemblyPath = Path.GetFullPath(args[1]);
            if (!File.Exists(Path.Combine(Managed, "Game.dll")) || !File.Exists(assemblyPath))
                throw new FileNotFoundException("Supply the installed Managed directory and intended mod DLL.");
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
                string dependency = Path.Combine(Managed, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(dependency) ? Assembly.LoadFrom(dependency) : null;
            };
            ModAssembly = Assembly.LoadFrom(assemblyPath);
            Console.WriteLine("QuantumStorage assembly: " + ModAssembly.GetName().Version);
            Console.WriteLine("QuantumStorage SHA256: " + Hash(assemblyPath));
            Console.WriteLine("Game SHA256: " + Hash(Path.Combine(Managed, "Game.dll")));
            MaterialPanelTests();
            Console.WriteLine($"PASS: {checks} panel checks. No Harmony detours, game callbacks, or rendering exercised.");
            return 0;
        }
        catch (Exception ex)
        {
            // Avoid accidentally placing host-specific paths in copied test output.
            Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            Console.Error.WriteLine("FAIL: " + cause.GetType().Name + ": " + cause.Message);
            return 1;
        }
    }

    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
        checks++;
    }

    // Intentionally unrelated static method used for fail-closed contract tests.
    static void PrintUiHash() => Console.WriteLine("Unreviewed test fixture");

    // Exercise the exact compiled production cache, not a test-side algorithm copy.
    sealed class PanelRows<TKey, TRow>
    {
        readonly object instance;
        readonly MethodInfo update;

        internal PanelRows(IList<KeyValuePair<TKey, int>> contents, IList<TRow> rows)
        {
            Type type = ModAssembly.GetType("Game.Systems.QuantumStorage.MaterialPanelRows`2", true)
                .MakeGenericType(typeof(TKey), typeof(TRow));
            instance = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { contents, rows }, null);
            update = type.GetMethod("TryUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        internal bool TryUpdate(IList<KeyValuePair<TKey, int>> contents,
            Func<TRow, bool> valid, Action<TRow, int> quantity, Action<TRow, int> position) =>
            (bool)update.Invoke(instance, new object[] { contents, valid, quantity, position });
    }
}

