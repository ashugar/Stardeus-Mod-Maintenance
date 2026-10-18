using System;
using System.Globalization;
using AutoUtilitiesModFramework;
using Game.Constants;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using KL.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ColonistAndBotsAutoUpgrade.UI
{
    public static class QualityThresholdView
    {
        private static readonly Color PanelColor =
          new Color(0.16f, 0.09f, 0.30f, 0.96f);
        private static readonly Color HeaderColor =
          new Color(0.20f, 0.12f, 0.36f, 1f);
        private static readonly Color TrackColor =
          new Color(0.045f, 0.03f, 0.07f, 1f);
        private static readonly Color FillColor =
          new Color(0.34f, 0.78f, 0.08f, 1f);
        private static readonly Color HandleColor =
          new Color(0.65f, 0.45f, 0.90f, 1f);
        public sealed class Result
        {
            public GameObject Root { get; set; }
        }

        public static Result Create(
          Lifetime lifetime,
          RectTransform parent,
          int siblingIndex,
          int initialValue,
          Action<int> onPreview,
          Action<int> onCommit,
          Func<bool> isCurrent,
          GameObject ownedRoot = null
        )
        {
            return CreateBounded(
                lifetime,
                parent,
                siblingIndex,
                initialValue,
                onPreview,
                onCommit,
                isCurrent,
                "CARAU_QualityThresholdEditor",
                "Quality Upgrade Threshold",
                0,
                100,
                "%",
                "Drag the slider or enter a percentage.",
                ownedRoot
            );
        }

        public static Result CreateBounded(
          Lifetime lifetime,
          RectTransform parent,
          int siblingIndex,
          int initialValue,
          Action<int> onPreview,
          Action<int> onCommit,
          Func<bool> isCurrent,
          string objectName,
          string titleLabel,
          int minimum,
          int maximum,
          string suffix,
          string hint,
          GameObject ownedRoot = null
        )
        {
            minimum = Math.Min(minimum, maximum);
            maximum = Math.Max(minimum, maximum);
            initialValue = Mathf.Clamp(initialValue, minimum, maximum);
            var root = ownedRoot ?? new GameObject(objectName);
            root.name = objectName;
            root.transform.SetParent(parent, false);
            root.transform.SetSiblingIndex(siblingIndex);

            var rootRect = root.AddComponent<RectTransform>();
            rootRect.localScale = Vector3.one;

            var rootImage = root.AddComponent<Image>();
            rootImage.color = PanelColor;
            rootImage.raycastTarget = false;
            FrameworkTheme.ApplyImage(rootImage, "PanelContent");

            var size = root.AddComponent<LayoutElement>();
            size.minHeight = 158f;
            size.preferredHeight = 158f;
            size.flexibleHeight = 0f;

            var header = AddPanel(
              rootRect,
              "Header",
              new Vector2(0f, 0.75f),
              Vector2.one,
              HeaderColor
            );
            FrameworkTheme.ApplyImage(
              header.GetComponent<Image>(),
              "PanelHeader"
            );
            var title = AddText(
              header,
              "Title",
              $"{titleLabel}: {initialValue}{suffix}",
              19f,
              TextAlignmentOptions.Center
            );

            AddText(
              rootRect,
              "Minimum",
              $"{minimum}{suffix}",
              14f,
              TextAlignmentOptions.Center,
              new Vector2(0.02f, 0.31f),
              new Vector2(0.08f, 0.62f)
            );
            AddText(
              rootRect,
              "Maximum",
              $"{maximum}{suffix}",
              14f,
              TextAlignmentOptions.Center,
              new Vector2(0.70f, 0.31f),
              new Vector2(0.78f, 0.62f)
            );

            var slider = CreateSlider(
              rootRect,
              new Vector2(0.08f, 0.34f),
              new Vector2(0.70f, 0.59f),
              initialValue,
              minimum,
              maximum
            );

            var nativeInput = UISpawn.Widget<WInput>(
              lifetime,
              UIPrefabId.WInput,
              rootRect,
              rootRect.childCount
            );
            nativeInput.Load(
              $"{minimum}-{maximum}",
              initialValue.ToString(),
              _ => { }
            );
            nativeInput.ToggleClearButton(false);

            var inputRect = nativeInput.Rect;
            SetAnchors(
              inputRect,
              rootRect,
              new Vector2(0.86f, 0.34f),
              new Vector2(0.93f, 0.62f)
            );
            var input = nativeInput.InputField;
            input.contentType = TMP_InputField.ContentType.DecimalNumber;
            input.characterLimit = 6;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.onFocusSelectAll = true;
            input.resetOnDeActivation = false;
            input.SetTextWithoutNotify(initialValue.ToString());
            if (!string.IsNullOrEmpty(suffix))
            {
                AddText(
                  rootRect,
                  "ValueSuffix",
                  suffix,
                  18f,
                  TextAlignmentOptions.MidlineLeft,
                  new Vector2(0.935f, 0.34f),
                  new Vector2(0.99f, 0.62f)
                );
            }

            AddText(
              rootRect,
              "Hint",
              hint,
              13f,
              TextAlignmentOptions.Center,
              new Vector2(0.04f, 0.04f),
              new Vector2(0.96f, 0.27f)
            );

            var currentValue = Mathf.Clamp(initialValue, minimum, maximum);
            var committing = false;

            void SetVisualValue(int value)
            {
                if (isCurrent?.Invoke() != true ||
                    root == null ||
                    title == null ||
                    slider == null ||
                    input == null)
                {
                    return;
                }

                currentValue = Mathf.Clamp(value, minimum, maximum);
                title.text =
                  $"{titleLabel}: {currentValue}{suffix}";
                slider.SetValueWithoutNotify(currentValue);
                input.SetTextWithoutNotify(currentValue.ToString());
            }

            slider.onValueChanged.AddListener(value => {
                if (isCurrent?.Invoke() != true)
                {
                    return;
                }

                var rounded = Mathf.Clamp(
                  Mathf.RoundToInt(value),
                  minimum,
                  maximum
                );
                SetVisualValue(rounded);
                onPreview?.Invoke(rounded);
            });

            var commitHandler =
              slider.gameObject.AddComponent<AutoUtilitiesModFramework.SliderCommitHandler>();
            commitHandler.OnCommit = () => {
                if (committing || isCurrent?.Invoke() != true)
                {
                    return;
                }

                committing = true;
                onCommit?.Invoke(currentValue);
            };

            void CommitInput(string value)
            {
                if (committing || isCurrent?.Invoke() != true)
                {
                    return;
                }

                var parsed = ParseBoundedInteger(
                    value,
                    currentValue,
                    minimum,
                    maximum
                );
                SetVisualValue(parsed);
                onPreview?.Invoke(parsed);
                committing = true;
                onCommit?.Invoke(parsed);
            }

            var inputCommitHandler =
              input.gameObject.GetComponent<
                AutoUtilitiesModFramework.InputCommitHandler
              >() ?? input.gameObject.AddComponent<
                AutoUtilitiesModFramework.InputCommitHandler
              >();
            inputCommitHandler.InputField = input;
            inputCommitHandler.OnCommit = CommitInput;

            var cleanup = root.AddComponent<QualityThresholdCleanup>();
            cleanup.Initialize(
              input,
              inputCommitHandler,
              slider,
              commitHandler
            );

            return new Result
            {
                Root = root
            };
        }

        private sealed class QualityThresholdCleanup : MonoBehaviour
        {
            private TMP_InputField input;
            private AutoUtilitiesModFramework.InputCommitHandler inputCommit;
            private Slider slider;
            private AutoUtilitiesModFramework.SliderCommitHandler sliderCommit;

            public void Initialize(
              TMP_InputField inputField,
              AutoUtilitiesModFramework.InputCommitHandler inputHandler,
              Slider thresholdSlider,
              AutoUtilitiesModFramework.SliderCommitHandler sliderHandler
            )
            {
                input = inputField;
                inputCommit = inputHandler;
                slider = thresholdSlider;
                sliderCommit = sliderHandler;
                input?.onSubmit.AddListener(OnSubmit);
            }

            private void OnSubmit(string value)
            {
                inputCommit?.RequestCommit();
            }

            private void OnDisable()
            {
                if (input != null)
                {
                    input.onSubmit.RemoveListener(OnSubmit);
                }

                if (inputCommit != null)
                {
                    inputCommit.OnCommit = null;
                    inputCommit.InputField = null;
                }

                slider?.onValueChanged.RemoveAllListeners();
                if (sliderCommit != null)
                {
                    sliderCommit.OnCommit = null;
                }

                input = null;
                inputCommit = null;
                slider = null;
                sliderCommit = null;
            }
        }

        private static int ParseBoundedInteger(
          string value,
          int fallback,
          int minimum,
          int maximum
        )
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            if (!float.TryParse(
                  value,
                  NumberStyles.Float,
                  CultureInfo.CurrentCulture,
                  out var parsed
                ) &&
                !float.TryParse(
                  value,
                  NumberStyles.Float,
                  CultureInfo.InvariantCulture,
                  out parsed
                ))
            {
                return fallback;
            }

            return Mathf.Clamp(
                Mathf.RoundToInt(parsed),
                minimum,
                maximum
            );
        }

        private static Slider CreateSlider(
          RectTransform parent,
          Vector2 anchorMin,
          Vector2 anchorMax,
          int value,
          int minimum,
          int maximum
        )
        {
            var sliderObject = new GameObject("ThresholdSlider");
            sliderObject.transform.SetParent(parent, false);
            var sliderRect = sliderObject.AddComponent<RectTransform>();
            SetAnchors(sliderRect, anchorMin, anchorMax);

            var background = AddPanel(
              sliderRect,
              "Background",
              new Vector2(0f, 0.42f),
              new Vector2(1f, 0.58f),
              TrackColor
            );

            var fillArea = AddPanel(
              sliderRect,
              "FillArea",
              new Vector2(0f, 0.42f),
              new Vector2(1f, 0.58f),
              Color.clear
            );
            fillArea.offsetMin = new Vector2(10f, 0f);
            fillArea.offsetMax = new Vector2(-10f, 0f);

            var fill = AddPanel(
              fillArea,
              "Fill",
              Vector2.zero,
              Vector2.one,
              FillColor
            );

            var handleArea = AddPanel(
              sliderRect,
              "HandleArea",
              Vector2.zero,
              Vector2.one,
              Color.clear
            );
            handleArea.offsetMin = new Vector2(10f, 0f);
            handleArea.offsetMax = new Vector2(-10f, 0f);

            var handle = AddPanel(
              handleArea,
              "Handle",
              new Vector2(0f, 0.15f),
              new Vector2(0f, 0.85f),
              HandleColor
            );
            handle.sizeDelta = new Vector2(18f, 0f);

            var slider = sliderObject.AddComponent<Slider>();
            slider.minValue = minimum;
            slider.maxValue = maximum;
            slider.wholeNumbers = true;
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.value = value;

            background.GetComponent<Image>().raycastTarget = false;
            fillArea.GetComponent<Image>().raycastTarget = false;
            fill.GetComponent<Image>().raycastTarget = false;
            handleArea.GetComponent<Image>().raycastTarget = false;
            handle.GetComponent<Image>().raycastTarget = true;

            FrameworkTheme.ApplySlider(
              slider,
              background.GetComponent<Image>(),
              fill.GetComponent<Image>(),
              handle.GetComponent<Image>()
            );

            return slider;
        }

        private static RectTransform AddPanel(
          RectTransform parent,
          string name,
          Vector2 anchorMin,
          Vector2 anchorMax,
          Color color
        )
        {
            var panelObject = new GameObject(name);
            panelObject.transform.SetParent(parent, false);
            var rect = panelObject.AddComponent<RectTransform>();
            SetAnchors(rect, anchorMin, anchorMax);

            var image = panelObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return rect;
        }

        private static TextMeshProUGUI AddText(
          RectTransform parent,
          string name,
          string value,
          float fontSize,
          TextAlignmentOptions alignment,
          Vector2? anchorMin = null,
          Vector2? anchorMax = null
        )
        {
            var textObject = new GameObject(name);
            textObject.transform.SetParent(parent, false);
            var rect = textObject.AddComponent<RectTransform>();
            SetAnchors(
              rect,
              anchorMin ?? Vector2.zero,
              anchorMax ?? Vector2.one
            );

            var text = textObject.AddComponent<TextMeshProUGUI>();
            text.text = value;
            text.alignment = alignment;
            text.fontSize = fontSize;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            FrameworkTheme.ApplyText(text, "Unsized");
            return text;
        }

        private static void SetAnchors(
          RectTransform rect,
          Vector2 anchorMin,
          Vector2 anchorMax
        )
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetAnchors(
          RectTransform rect,
          RectTransform parent,
          Vector2 anchorMin,
          Vector2 anchorMax
        )
        {
            rect.SetParent(parent, false);
            SetAnchors(rect, anchorMin, anchorMax);
        }
    }
}
