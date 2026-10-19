extern alias panelcecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using panelcecil::Mono.Cecil;

static partial class Program
{
    // Structural checks of the installed binary, not a substitute for clicking
    // pooled Unity widgets in game. No Core callback is invoked by these checks.
    static void MaterialPanelCoreContractTests()
    {
        using var module = ModuleDefinition.ReadModule(Path.Combine(Managed, "Game.dll"));
        var tab = module.Types.Single(t => t.FullName == "Game.Systems.UIUX.MatStorageTab");
        var methods = NestedTypes(tab).SelectMany(t => t.Methods)
            .Where(m => m.Name.Contains("<BuildContentsList>")).ToArray();
        Check(methods.Length == 3, "exact reviewed native contents callback set exists in installed Core");
        var calls = methods.ToDictionary(m => m, Calls);
        var menus = calls.Where(p => p.Value.Any(c => c.Name == "QuantityChoiceOptions")).ToArray();
        Check(menus.Length == 1, "one native quantity-menu callback");
        Check(menus[0].Value.Any(c => c.Name == "get_Contents") &&
            menus[0].Value.Any(c => c.Name == "TryGetValue"),
            "native menu reads live contents rather than retaining the initial quantity");
        Check(menus[0].Value.Any(c => c.Name == "QuantityChoiceLabel"),
            "native quantity choice labels retained");
        var ejects = calls.Where(p => p.Value.Any(c => c.Name == "Eject")).ToArray();
        Check(ejects.Length == 1, "one native material-ejection callback");
        Check(ejects[0].Value.Any(c => c.Name == "set_IsForbidden") &&
            ejects[0].Value.Any(c => c.Name == "UpdateForbiddenIcon"),
            "native ejected-material general forbidden policy remains authoritative");
        var pile = module.Types.Single(t => t.FullName == "Game.Components.UnstoredMatComp");
        var forbidSetter = pile.Methods.Single(m => m.Name == "set_IsForbidden" && m.Parameters.Count == 1);
        Check(Calls(forbidSetter).Any(c => c.DeclaringType.FullName == "Game.Systems.Inventory.InventorySys" &&
            c.Name == "OnPileForbidChanged"),
            "native general-forbid setter informs the authoritative inventory index");
        Check(Calls(forbidSetter).Any(c => c.Name == "UpdateForbiddenIcon"),
            "native general-forbid setter refreshes its icon");
        Check(!ejects[0].Value.Any(c => c.DeclaringType?.Namespace == "Game.Systems.UIUX" ||
            c.DeclaringType?.Namespace?.StartsWith("UnityEngine") == true),
            "eject callback does not mutate a row returned to the widget pool");
        Check(calls.Any(p => p.Value.Any(c => c.Name == "DumpContents") &&
            p.Value.Any(c => c.Name == "ReloadTab")), "native Eject All retains full tab reload");
        var build = tab.Methods.Single(m => m.Name == "BuildContentsList");
        var buildCalls = Calls(build);
        // IconId is a Core field (ldfld), unlike the NameT property getter.
        // Validate both operand kinds rather than expecting a nonexistent getter.
        var buildFieldReads = build.Body.Instructions
            .Where(i => i.OpCode.Code == panelcecil::Mono.Cecil.Cil.Code.Ldfld)
            .Select(i => i.Operand).OfType<FieldReference>().ToArray();
        Check(buildCalls.Any(c => c.Name == "get_SortedContents") && buildCalls.Any(c => c.Name == "get_NameT") &&
            buildFieldReads.Any(f => f.DeclaringType.FullName == "Game.Constants.MatType" && f.Name == "IconId") &&
            buildCalls.Any(c => c.DeclaringType.FullName == "Game.Utils.Units" && c.Name == "Num"),
            "native full sorted material rows retain their names, icons and quantities");
        Check(buildCalls.Any(c => c.Name == "Dispose") && buildCalls.Any(c => c.Name == "CreateChild"),
            "structural rebuild retains native contents lifetime replacement");
        Check(buildCalls.Any(c => c.Name == "AddCodexTooltip"), "native row tooltips retained");
        Check(methods.Select(m => m.DeclaringType).SelectMany(t => t.Fields)
            .All(f => f.FieldType.FullName != "Game.Systems.UIUX.WButtonIconText2"),
            "native action closures do not capture pooled material row widgets");
        var filterCalls = Calls(tab.Methods.Single(m => m.Name == "ToggleFilterPanel"));
        Check(filterCalls.Any(c => c.Name == "IsShowingFor") && filterCalls.Any(c => c.Name == "Close") &&
            filterCalls.Any(c => c.Name == "CreateLifetime"), "Core filter subpanel owns its current cleanup lifetime");
    }

    static IEnumerable<TypeDefinition> NestedTypes(TypeDefinition type)
    {
        yield return type;
        foreach (var child in type.NestedTypes)
            foreach (var nested in NestedTypes(child)) yield return nested;
    }

    static MethodReference[] Calls(MethodDefinition method) => method.Body.Instructions
        .Select(i => i.Operand).OfType<MethodReference>().ToArray();
}
