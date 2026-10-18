using Game.Data;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace ColonistAndBotsAutoUpgrade.UI
{
    internal sealed class DeferredBeingPortraitLoader : MonoBehaviour
    {
        private const int MaxAttempts = 30;

        private GameState S;
        private Being being;
        private RawImage portrait;
        private AspectRatioFitter aspect;

        public void Initialize(
          GameState state,
          Being targetBeing,
          RawImage targetPortrait,
          AspectRatioFitter targetAspect
        )
        {
            S = state;
            being = targetBeing;
            portrait = targetPortrait;
            aspect = targetAspect;
            StartCoroutine(LoadWhenReady());
        }

        private IEnumerator LoadWhenReady()
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                yield return new WaitForEndOfFrame();

                if (portrait == null || being?.Data == null)
                {
                    yield break;
                }

                var avatar = S.Query.GetBeingAvatar.Ask(being.Data);

                if (avatar?.Tex == null)
                {
                    continue;
                }

                BeingPortraitView.ApplyTexture(
                    portrait,
                    aspect,
                    avatar.Tex
                );
                yield break;
            }
        }
    }

    internal static class BeingPortraitView
    {
        public static bool Create(
          RectTransform parent,
          GameState state,
          Being being
        )
        {
            if (being?.Data == null)
            {
                return false;
            }

            var portraitObject = new GameObject("BeingPortrait");
            portraitObject.transform.SetParent(parent, false);

            var portraitRect =
                portraitObject.AddComponent<RectTransform>();
            portraitRect.anchorMin = new Vector2(0f, 0.5f);
            portraitRect.anchorMax = new Vector2(0f, 0.5f);
            portraitRect.pivot = new Vector2(0f, 0.5f);
            portraitRect.anchoredPosition = new Vector2(7f, 0f);
            portraitRect.sizeDelta = new Vector2(46f, 46f);

            var portrait = portraitObject.AddComponent<RawImage>();
            portrait.color = Color.white;
            portrait.raycastTarget = false;
            portrait.enabled = false;

            var aspect =
                portraitObject.AddComponent<AspectRatioFitter>();
            aspect.aspectMode =
                AspectRatioFitter.AspectMode.FitInParent;
            aspect.aspectRatio = 1f;

            var avatar = state.Query.GetBeingAvatar.Ask(being.Data);

            if (avatar?.Tex != null)
            {
                ApplyTexture(portrait, aspect, avatar.Tex);
            }
            else
            {
                portraitObject
                    .AddComponent<DeferredBeingPortraitLoader>()
                    .Initialize(state, being, portrait, aspect);
            }

            return true;
        }

        internal static void ApplyTexture(
          RawImage portrait,
          AspectRatioFitter aspect,
          Texture texture
        )
        {
            portrait.texture = texture;
            portrait.enabled = true;
            aspect.aspectRatio = texture.height > 0
                ? (float)texture.width / texture.height
                : 1f;
        }
    }
}
