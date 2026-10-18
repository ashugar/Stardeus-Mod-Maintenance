using AutoUtilitiesModFramework;
using TMPro;
using UnityEngine;

namespace ColonistAndBotsAutoUpgrade.UI
{
    internal static class CompactStateCardLayout
    {
        private const float HeaderBottom = 0.55f;
        private const float StateBottom = 0.18f;
        private const float StateFontSize = 14f;

        public static void Apply(FrameworkStateCard card)
        {
            if (card == null)
            {
                return;
            }

            var titleText = card.TitleText;
            var detailText = card.DetailText;
            var headerRect = titleText?.rectTransform.parent as RectTransform;
            if (headerRect != null)
            {
                headerRect.anchorMin = new Vector2(0f, HeaderBottom);
                headerRect.anchorMax = Vector2.one;
            }

            TMP_Text stateText = null;
            var textComponents = card.GetComponentsInChildren<TMP_Text>(true);
            foreach (var textComponent in textComponents)
            {
                if (textComponent != titleText && textComponent != detailText)
                {
                    stateText = textComponent;
                    break;
                }
            }

            if (detailText != null)
            {
                detailText.gameObject.SetActive(false);
            }

            if (stateText == null)
            {
                return;
            }

            var stateRect = stateText.rectTransform;
            stateRect.anchorMin = new Vector2(0f, StateBottom);
            stateRect.anchorMax = new Vector2(1f, HeaderBottom);
            stateText.fontSize = StateFontSize;
        }
    }
}
