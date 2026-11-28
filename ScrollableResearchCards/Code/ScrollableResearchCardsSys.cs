using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Data;
using Game.Research;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using HarmonyLib;
using KL.Utils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Systems.ScrollableResearchCards
{
    public sealed class ScrollableResearchCardsSys : GameSystem
    {
        private const string SystemId = "ScrollableResearchCardsSys";
        private const string HarmonyId = "jleas.stardeus.scrollableresearchcards";
        private static readonly HashSet<string> Reported = new HashSet<string>();

        private static FieldInfo researchable;
        private static FieldInfo container;
        private static FieldInfo block;
        private static FieldInfo background;
        private static FieldInfo prefab;
        private static FieldInfo extraTile;
        private static FieldInfo definitions;
        private static FieldInfo effects;
        private static FieldInfo raycastables;
        private Harmony harmony;

        public override string Id => SystemId;
        public override bool SkipInSandbox => false;

        [SelfInit]
        private static void Register() => GameSystems.Register(SystemId, () => new ScrollableResearchCardsSys());

        protected override void OnInitialize()
        {
            var patcher = new Harmony(HarmonyId);
            try
            {
                var method = LegacyUnlocks.RequireLegacyMethod();
                if (Harmony.GetPatchInfo(method)?.Transpilers.Any(p => p.owner != HarmonyId) == true)
                    throw new InvalidOperationException("another mod transpiles the Core unlock renderer");

                researchable = RequireField<Researchable>("researchable");
                container = RequireField<WGroup>("unlocksContainer");
                block = RequireField<GameObject>("unlocksBlock");
                background = RequireField<Image>("background");
                prefab = RequireField<WImage>("unlockPrefab");
                extraTile = RequireField<WText>("extraUnlocksText");
                definitions = RequireField<Dictionary<Def, WImage>>("spawnedUnlockDefs");
                effects = RequireField<Dictionary<IResearchUnlockEffect, WImage>>("spawnedUnlockEffects");
                raycastables = RequireField<List<Graphic>>("itemRaycastables");

                patcher.Patch(method, postfix: new HarmonyMethod(
                    AccessTools.DeclaredMethod(typeof(ScrollableResearchCardsSys), nameof(AfterCreateUnlocks)))
                    { priority = Priority.Last });
                harmony = patcher;
            }
            catch (Exception error)
            {
                try
                {
                    patcher.UnpatchAll(HarmonyId);
                }
                catch (Exception cleanup)
                {
                    WarnOnce("patch cleanup", cleanup.Message);
                }
                WarnOnce("Core compatibility", error.GetBaseException().Message);
            }
        }

        public override void Unload()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
        }

        private static FieldInfo RequireField<T>(string name)
        {
            var field = AccessTools.DeclaredField(typeof(WResearchNode), name);
            if (field == null || field.IsStatic || field.FieldType != typeof(T))
                throw new MissingFieldException(typeof(WResearchNode).FullName, name);
            return field;
        }

        private static T Read<T>(FieldInfo field, WResearchNode node) => (T)field.GetValue(node);

        internal static bool FirstWarning(string category, string reason) =>
            Reported.Add(category + ": " + reason);

        private static void WarnOnce(string category, string reason)
        {
            if (FirstWarning(category, reason))
                D.Warn("Scrollable Research Cards kept Core's unlock display ({0}): {1}", category, reason);
        }

        private static void AfterCreateUnlocks(WResearchNode __instance)
        {
            try
            {
                var node = __instance;
                var grid = Read<WGroup>(container, node).Rect;
                var unlockBlock = Read<GameObject>(block, node).transform;
                if (grid.GetComponentInChildren<ResearchUnlockPane>(true))
                    return;
                if (HasScrollOwner(grid, unlockBlock, out var owner))
                {
                    WarnOnce("pane ownership", owner);
                    return;
                }

                if (!Read<WText>(extraTile, node))
                    return;

                var def = Read<Researchable>(researchable, node).Def;
                var unlocks = A.S.Research.GetUnlockableIconsFor(def);
                if (unlocks.Count > ResearchUnlockPane.Capacity)
                    InstallPane(node, grid, unlocks, def.IsInSeason);
            }
            catch (Exception error)
            {
                WarnOnce("pane setup", error.GetBaseException().Message);
            }
        }

        private static bool HasScrollOwner(RectTransform grid, Transform unlockBlock, out string owner)
        {
            if (!grid.IsChildOf(unlockBlock))
                throw new InvalidOperationException("the unlock grid is outside its native unlock block");

            var ownedComponents = grid.GetComponentsInChildren<Component>(true).AsEnumerable();
            for (var parent = grid.parent; parent && parent.IsChildOf(unlockBlock); parent = parent.parent)
                ownedComponents = ownedComponents.Concat(parent.GetComponents<Component>());

            var scroll = ownedComponents.FirstOrDefault(c => c &&
                (c is ScrollRect || c is IReactsToScroll || c is IScrollHandler));
            if (!scroll)
            {
                owner = null;
                return false;
            }

            owner = "existing unlock scrolling detected inside the pane (" + scroll.GetType().FullName + ")";
            return true;
        }

        private static void InstallPane(WResearchNode node, RectTransform grid,
            List<(Def def, IResearchUnlockEffect effect)> unlocks, bool inSeason)
        {
            var nativeTile = Read<WText>(extraTile, node);
            var nativeGraphics = Read<List<Graphic>>(raycastables, node);
            var nativeDefs = Read<Dictionary<Def, WImage>>(definitions, node);
            var nativeEffects = Read<Dictionary<IResearchUnlockEffect, WImage>>(effects, node);
            var slots = new List<GameObject>();
            foreach (Transform child in grid)
            {
                if (child.gameObject.activeSelf)
                    slots.Add(child.gameObject);
            }

            if (!nativeTile || slots.Count != ResearchUnlockPane.Capacity ||
                slots[slots.Count - 1] != nativeTile.gameObject ||
                slots.Take(slots.Count - 1).Any(s => !s.GetComponent<WImage>()) ||
                slots.Any(s => !nativeGraphics.Contains(s.GetComponent<Graphic>())))
                throw new InvalidOperationException("the live grid did not contain 11 native icons and the overflow tile");

            if (slots.Any(s => s.GetComponentsInChildren<CanvasGroup>(true).Any(g => g.ignoreParentGroups)))
                throw new InvalidOperationException("a native unlock slot ignores parent CanvasGroups");

            var layout = GridMetrics.Read(grid);
            var originalDefs = nativeDefs.ToArray();
            var originalEffects = nativeEffects.ToArray();
            var icons = new CanvasGroup[unlocks.Count];
            var addedGraphics = new Graphic[unlocks.Count];
            var stagedDefs = new Dictionary<Def, WImage>();
            var stagedEffects = new Dictionary<IResearchUnlockEffect, WImage>();
            var stockVisibility = new List<CanvasGroup>();
            GameObject pane = null;
            bool updatingNative = false;
            try
            {
                pane = new GameObject("Scrollable unlocks", typeof(RectTransform));
                pane.SetActive(false);
                var viewport = (RectTransform)pane.transform;
                viewport.SetParent(grid, false);
                Stretch(viewport);
                pane.AddComponent<LayoutElement>().ignoreLayout = true;
                pane.AddComponent<RectMask2D>();
                AddImage(viewport, new Color(0, 0, 0, 0), raycast: true);

                var content = Child(viewport, "Unlock icons");
                content.anchorMin = content.anchorMax = content.pivot = new Vector2(0, 1);
                content.sizeDelta = Vector2.zero;
                var iconPrefab = Read<WImage>(prefab, node);
                for (int i = 0; i < unlocks.Count; i++)
                {
                    var entry = unlocks[i];
                    var icon = UISpawn.WidgetPermanent(iconPrefab, content);
                    layout.Place(icon.Rect, i);
                    var graphic = icon.GetComponent<Graphic>();
                    if (!graphic || icon.GetComponentsInChildren<CanvasGroup>(true).Any(g => g.ignoreParentGroups))
                        throw new InvalidOperationException("the native icon prefab has incompatible raycast components");
                    graphic.raycastTarget = false;
                    addedGraphics[i] = graphic;
                    icons[i] = icon.gameObject.AddComponent<CanvasGroup>();
                    if (!inSeason)
                        icon.SetSprite(Images.Sprite("Icons/Color/Question"));
                    else if (entry.def != null)
                    {
                        icon.SetSprite(entry.def.PreviewSprite);
                        stagedDefs[entry.def] = icon;
                    }
                    else if (entry.effect != null)
                    {
                        icon.SetImage(entry.effect.Icon);
                        stagedEffects[entry.effect] = icon;
                    }
                    else
                        throw new InvalidOperationException("Core returned an unlock without a definition or research effect");
                }

                var track = Child(viewport, "Scroll track");
                layout.PlaceTrack(track);
                AddImage(track, new Color(0.10f, 0.12f, 0.15f, 0.9f), raycast: false);
                var thumb = Child(track, "Scroll position");
                AddImage(thumb, new Color(0.66f, 0.73f, 0.8f, 0.95f), raycast: false);
                pane.AddComponent<ResearchUnlockPane>().Configure(icons, content, thumb,
                    node.CanvasGroup, Read<Image>(background, node), layout.RowHeight);
                foreach (var slot in slots)
                    stockVisibility.Add(slot.AddComponent<CanvasGroup>());

                updatingNative = true;
                ReplaceEntries(nativeDefs, stagedDefs);
                ReplaceEntries(nativeEffects, stagedEffects);
                nativeGraphics.AddRange(addedGraphics);
                extraTile.SetValue(node, null);
                pane.SetActive(true);
                foreach (var slot in stockVisibility)
                {
                    slot.alpha = 0;
                    slot.blocksRaycasts = false;
                }
            }
            catch
            {
                if (pane)
                    pane.SetActive(false);
                foreach (var slot in stockVisibility)
                {
                    slot.alpha = 1;
                    slot.blocksRaycasts = true;
                    UnityEngine.Object.Destroy(slot);
                }

                if (updatingNative)
                {
                    ReplaceEntries(nativeDefs, originalDefs);
                    ReplaceEntries(nativeEffects, originalEffects);
                    foreach (var graphic in addedGraphics)
                        nativeGraphics.Remove(graphic);
                    extraTile.SetValue(node, nativeTile);
                }
                if (pane)
                    UnityEngine.Object.Destroy(pane);
                throw;
            }
        }

        private static void ReplaceEntries<TKey>(Dictionary<TKey, WImage> map,
            IEnumerable<KeyValuePair<TKey, WImage>> entries)
        {
            map.Clear();
            foreach (var entry in entries)
                map.Add(entry.Key, entry.Value);
        }

        private static RectTransform Child(RectTransform parent, string name)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void AddImage(RectTransform rect, Color color, bool raycast)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = raycast;
        }
    }
}




