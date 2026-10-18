using Game.Data;
using Game.Constants;
using Game.Systems.Inventory;
using LocalLogMod.Logging;

namespace LocalLogMod.Inventory {
    internal static class InventoryObserver {
        internal static void Attach(GameState state) {
            state.Sig.MatProduced.AddListener(MatProduced);
            state.Sig.MatConsumed.AddListener(MatConsumed);
            state.Sig.ObjProduced.AddListener(ObjProduced);
            state.Sig.ObjConsumed.AddListener(ObjConsumed);
        }

        internal static void Detach(GameState state) {
            if (state == null) { return; }
            state.Sig.MatProduced.RemoveListener(MatProduced);
            state.Sig.MatConsumed.RemoveListener(MatConsumed);
            state.Sig.ObjProduced.RemoveListener(ObjProduced);
            state.Sig.ObjConsumed.RemoveListener(ObjConsumed);
        }

        private static void MatProduced(MatType type, int amount) {
            SessionLog.Guard("inventory.mat.produced", () => SessionLog.Write("Inventory",
                "material_produced id=" + type?.Id + " amount=" + amount));
        }

        private static void MatConsumed(MatType type, int amount) {
            SessionLog.Guard("inventory.mat.consumed", () => SessionLog.Write("Inventory",
                "material_consumed id=" + type?.Id + " amount=" + amount));
        }

        private static void ObjProduced(Def def, int amount) {
            SessionLog.Guard("inventory.obj.produced", () => SessionLog.Write("Inventory",
                "object_produced id=" + def?.Id + " amount=" + amount));
        }

        private static void ObjConsumed(Def def, int amount) {
            SessionLog.Guard("inventory.obj.consumed", () => SessionLog.Write("Inventory",
                "object_consumed id=" + def?.Id + " amount=" + amount));
        }
    }
}
