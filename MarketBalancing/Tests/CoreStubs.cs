// These stand-ins exercise adapter policy and ownership only. They do not
// simulate Harmony, native market initialization, or gameplay.
namespace Game.CodeGen { }
namespace Game.Utils {
    internal sealed class SelfInitAttribute : System.Attribute { }
}
namespace KL.Utils {
    internal static class D {
        internal static readonly List<string> Messages = new();
        internal static void Warn(string text, params object[] args) => Messages.Add(string.Format(text, args));
        internal static void Log(string text, params object[] args) => Messages.Add(string.Format(text, args));
        internal static void LogEx(Exception ex, string text) => Messages.Add(text + ex.Message);
    }
}
namespace Game.Systems {
    public abstract class GameSystem {
        public abstract string Id { get; }
        protected abstract void OnInitialize();
        public abstract void Unload();
    }
    internal static class GameSystems {
        internal static void Register(string id, Func<GameSystem> create) { }
    }
    internal sealed class EconomySys { }
}
namespace HarmonyLib {
    internal sealed class Harmony {
        internal Harmony(string id) { }
        internal void PatchAll(System.Reflection.Assembly assembly) { }
        internal void UnpatchAll(string id) { }
    }
    internal sealed class HarmonyPatch : Attribute {
        internal HarmonyPatch(Type type, string method, Type[] args) { }
    }
    internal sealed class HarmonyPrefix : Attribute { }
}
namespace Game.Data {
    internal sealed class Def {
        internal string Id;
        internal bool IgnoreMarket;
        internal MatType MatType;
    }
    internal sealed class MatType {
        internal Def Def;
        internal bool IgnoreMarket;
    }
    internal struct Mat {
        internal MatType Type;
        internal int StackSize;
    }
}
namespace Game.Data.Economy {
    internal sealed class MarketItem {
        internal static readonly Dictionary<int, MarketItem> CFDs = new();
        internal string Id;
        internal int IdH;
        internal Game.Data.Def Def;
        internal float Value;
    }
}
namespace Game.Systems.Crafting {
    internal sealed class Craftable {
        internal static readonly List<Craftable> All = new();
        internal string Id;
        internal string Type;
        internal string ProductDefId;
        internal int OutputMultiplier;
        internal bool IgnoreMarket;
        internal Game.Data.Mat[] Ingredients;
    }
}
