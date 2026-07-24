using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FeedLikeGrandma;

internal readonly struct FeedHoverFoodRows
{
    internal FeedHoverFoodRows(
        IReadOnlyList<ResolvedFoodEntry> levelUpFoods,
        IReadOnlyList<ResolvedFoodEntry> dailyFoods,
        int currentLevel)
    {
        LevelUpFoods = levelUpFoods;
        DailyFoods = dailyFoods;
        CurrentLevel = currentLevel;
    }

    internal IReadOnlyList<ResolvedFoodEntry> LevelUpFoods { get; }
    internal IReadOnlyList<ResolvedFoodEntry> DailyFoods { get; }
    internal int CurrentLevel { get; }
    internal int TotalCount => LevelUpFoods.Count + DailyFoods.Count;
}

internal static class FeedHoverHud
{
    private const float IconSpacing = 6f;
    private const float IconOffsetY = 6f;
    private static TMP_FontAsset? _cachedHudFont;
    private static HoverFoodIconHud? _hud;

    internal static void Update(Hud? hud, FeedHoverFoodRows? rows)
    {
        if (hud == null || hud.m_rootObject == null)
        {
            DestroyHud();
            return;
        }

        if (_hud == null || !_hud.IsValidFor(hud))
        {
            DestroyHud();
            _hud = new HoverFoodIconHud(hud);
        }

        if (!rows.HasValue || rows.Value.TotalCount == 0)
        {
            _hud.Hide();
            return;
        }

        _hud.Show(rows.Value);
    }

    internal static void Shutdown()
    {
        DestroyHud();
        _cachedHudFont = null;
    }

    private static void DestroyHud()
    {
        _hud?.Destroy();
        _hud = null;
    }

    private static void ApplyHudFont(TMP_Text target, TMP_Text? preferred = null)
    {
        if (preferred != null && preferred.font != null)
        {
            target.font = preferred.font;
            if (preferred.fontSharedMaterial != null)
            {
                target.fontSharedMaterial = preferred.fontSharedMaterial;
            }

            return;
        }

        TMP_FontAsset? font = ResolveHudFont();
        if (font != null)
        {
            target.font = font;
        }
    }

    private static TMP_FontAsset? ResolveHudFont()
    {
        if (_cachedHudFont != null)
        {
            return _cachedHudFont;
        }

        if (Hud.instance != null && Hud.instance.m_hoverName != null && Hud.instance.m_hoverName.font != null)
        {
            return _cachedHudFont = Hud.instance.m_hoverName.font;
        }

        foreach (TMP_FontAsset font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (font != null && font.name == "Valheim-AveriaSansLibre")
            {
                return _cachedHudFont = font;
            }
        }

        if (TMP_Settings.defaultFontAsset != null)
        {
            return _cachedHudFont = TMP_Settings.defaultFontAsset;
        }

        foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text != null && text.font != null)
            {
                return _cachedHudFont = text.font;
            }
        }

        foreach (TMP_FontAsset font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (font != null)
            {
                return _cachedHudFont = font;
            }
        }

        return null;
    }

    private static bool Approximately(float left, float right)
    {
        return Mathf.Abs(left - right) < 0.001f;
    }

    private sealed class HoverFoodIconHud
    {
        private readonly List<HoverFoodIcon> _levelUpIcons = new();
        private readonly List<HoverFoodIcon> _dailyIcons = new();
        private readonly Hud _ownerHud;
        private readonly GameObject _ownerRoot;
        private readonly GameObject _root;
        private readonly RectTransform _rootTransform;
        private readonly GameObject? _levelUpRow;
        private readonly RectTransform? _levelUpRowTransform;
        private readonly HoverFoodRowLabel? _levelUpLabel;
        private readonly GameObject? _dailyRow;
        private readonly RectTransform? _dailyRowTransform;
        private IReadOnlyList<ResolvedFoodEntry>? _shownLevelUpFoods;
        private IReadOnlyList<ResolvedFoodEntry>? _shownDailyFoods;
        private int _shownCurrentLevel;
        private string _shownLanguage = string.Empty;
        private float _shownIconSize = -1f;
        private bool _contentShown;
        private float _nextAnchorUpdateTime;

        internal HoverFoodIconHud(Hud ownerHud)
        {
            _ownerHud = ownerHud;
            _ownerRoot = ownerHud.m_rootObject;
            _root = new GameObject(
                "FeedLikeGrandmaHoverIcons",
                typeof(RectTransform),
                typeof(VerticalLayoutGroup),
                typeof(ContentSizeFitter));
            _rootTransform = _root.GetComponent<RectTransform>();
            _rootTransform.SetParent(_ownerRoot.transform, false);
            _rootTransform.anchorMin = new Vector2(0.5f, 0.5f);
            _rootTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _rootTransform.pivot = new Vector2(0f, 0f);

            VerticalLayoutGroup layout = _root.GetComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.spacing = GetRowSpacing();

            ContentSizeFitter fitter = _root.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _levelUpRow = CreateRow("LevelUp", _rootTransform, out _levelUpRowTransform);
            _levelUpLabel = HoverFoodRowLabel.Create(_levelUpRowTransform);
            _dailyRow = CreateRow("Daily", _rootTransform, out _dailyRowTransform);
            _root.SetActive(false);
        }

        internal bool IsValidFor(Hud? hud)
        {
            return hud != null &&
                   _ownerHud == hud &&
                   _ownerRoot == hud.m_rootObject &&
                   _root != null &&
                   _rootTransform != null;
        }

        internal void Show(FeedHoverFoodRows rows)
        {
            if (!IsValidFor(Hud.instance))
            {
                return;
            }

            float iconSize = FeedLikeGrandmaPlugin.FoodIconSize.Value;
            string language = Localization.instance == null
                ? string.Empty
                : Localization.instance.GetSelectedLanguage();
            bool contentChanged = !_contentShown ||
                                  !ReferenceEquals(_shownLevelUpFoods, rows.LevelUpFoods) ||
                                  !ReferenceEquals(_shownDailyFoods, rows.DailyFoods) ||
                                  _shownCurrentLevel != rows.CurrentLevel ||
                                  !string.Equals(_shownLanguage, language, StringComparison.Ordinal) ||
                                  !Approximately(_shownIconSize, iconSize);
            if (contentChanged)
            {
                _root.GetComponent<VerticalLayoutGroup>().spacing = GetRowSpacing();
                string levelUpLabel = rows.CurrentLevel > 0
                    ? FeedLocalization.Format(
                        "$flg_food_icon_level_label",
                        rows.CurrentLevel.ToString(CultureInfo.InvariantCulture),
                        (rows.CurrentLevel + 1).ToString(CultureInfo.InvariantCulture))
                    : string.Empty;
                ShowRow(
                    _levelUpRow,
                    _levelUpRowTransform,
                    _levelUpIcons,
                    rows.LevelUpFoods,
                    _levelUpLabel,
                    levelUpLabel,
                    iconSize);
                ShowRow(
                    _dailyRow,
                    _dailyRowTransform,
                    _dailyIcons,
                    rows.DailyFoods,
                    label: null,
                    labelText: string.Empty,
                    iconSize);
                _shownLevelUpFoods = rows.LevelUpFoods;
                _shownDailyFoods = rows.DailyFoods;
                _shownCurrentLevel = rows.CurrentLevel;
                _shownLanguage = language;
                _shownIconSize = iconSize;
                _contentShown = true;
            }

            if (!_root.activeSelf || contentChanged || Time.unscaledTime >= _nextAnchorUpdateTime)
            {
                _rootTransform.anchoredPosition = GetHoverTextIconAnchor();
                _nextAnchorUpdateTime = Time.unscaledTime + 0.1f;
            }

            SetActiveIfChanged(_root, rows.TotalCount > 0);
        }

        internal void Hide()
        {
            SetActiveIfChanged(_root, false);
        }

        internal void Destroy()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
        }

        private static GameObject CreateRow(string name, RectTransform parent, out RectTransform rowTransform)
        {
            GameObject rowObject = new(
                $"FeedLikeGrandma{name}FoodRow",
                typeof(RectTransform),
                typeof(HorizontalLayoutGroup),
                typeof(ContentSizeFitter));
            rowTransform = rowObject.GetComponent<RectTransform>();
            rowTransform.SetParent(parent, false);

            HorizontalLayoutGroup layout = rowObject.GetComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.spacing = IconSpacing;

            ContentSizeFitter fitter = rowObject.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            rowObject.SetActive(false);
            return rowObject;
        }

        private static void ShowRow(
            GameObject? rowObject,
            RectTransform? rowTransform,
            List<HoverFoodIcon> icons,
            IReadOnlyList<ResolvedFoodEntry> foods,
            HoverFoodRowLabel? label,
            string labelText,
            float iconSize)
        {
            if ((object?)rowObject == null || (object?)rowTransform == null)
            {
                return;
            }

            EnsureIconCount(rowTransform, icons, foods.Count);
            rowObject.GetComponent<HorizontalLayoutGroup>().spacing = IconSpacing;

            for (int i = 0; i < icons.Count; i++)
            {
                bool active = i < foods.Count;
                SetActiveIfChanged(icons[i].GameObject, active);
                if (!active)
                {
                    continue;
                }

                RectTransform iconTransform = icons[i].RectTransform;
                iconTransform.sizeDelta = Vector2.one * iconSize;
                icons[i].SetFood(foods[i], iconSize);
            }

            label?.SetText(labelText, iconSize);
            SetActiveIfChanged(rowObject, foods.Count > 0);
        }

        private static void EnsureIconCount(RectTransform parent, List<HoverFoodIcon> icons, int count)
        {
            while (icons.Count < count)
            {
                icons.Add(HoverFoodIcon.Create(parent, icons.Count));
            }
        }

        private static float GetRowSpacing()
        {
            return Mathf.Max(1f, IconSpacing * 0.35f);
        }

        private static void SetActiveIfChanged(GameObject? gameObject, bool active)
        {
            if (gameObject != null && gameObject.activeSelf != active)
            {
                gameObject.SetActive(active);
            }
        }

        private static Vector2 GetHoverTextIconAnchor()
        {
            Hud? hud = Hud.instance;
            if (hud == null || hud.m_hoverName == null || hud.m_rootObject == null)
            {
                return new Vector2(0f, IconOffsetY);
            }

            RectTransform? parent = hud.m_rootObject.transform as RectTransform;
            RectTransform hoverTransform = hud.m_hoverName.rectTransform;
            if (parent == null || hoverTransform == null)
            {
                return new Vector2(0f, IconOffsetY);
            }

            hud.m_hoverName.ForceMeshUpdate();
            Bounds textBounds = hud.m_hoverName.textBounds;
            Vector3 topLeftWorld;
            if (textBounds.size.sqrMagnitude > 0.001f)
            {
                topLeftWorld = hoverTransform.TransformPoint(new Vector3(textBounds.min.x, textBounds.max.y, 0f));
            }
            else
            {
                Vector3[] corners = new Vector3[4];
                hoverTransform.GetWorldCorners(corners);
                topLeftWorld = corners[1];
            }

            Vector3 topLeftLocal = parent.InverseTransformPoint(topLeftWorld);
            return new Vector2(topLeftLocal.x, topLeftLocal.y + IconOffsetY);
        }
    }

    private sealed class HoverFoodRowLabel
    {
        private readonly GameObject _gameObject;
        private readonly RectTransform _rectTransform;
        private readonly TextMeshProUGUI _text;
        private readonly LayoutElement _layoutElement;
        private string _lastText = string.Empty;
        private float _lastIconSize = -1f;

        private HoverFoodRowLabel(
            GameObject gameObject,
            RectTransform rectTransform,
            TextMeshProUGUI text,
            LayoutElement layoutElement)
        {
            _gameObject = gameObject;
            _rectTransform = rectTransform;
            _text = text;
            _layoutElement = layoutElement;
        }

        internal static HoverFoodRowLabel Create(RectTransform parent)
        {
            GameObject labelObject = new(
                "FeedLikeGrandmaLevelUpLabel",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(LayoutElement));
            RectTransform rectTransform = labelObject.GetComponent<RectTransform>();
            rectTransform.SetParent(parent, false);

            TextMeshProUGUI text = labelObject.AddComponent<TextMeshProUGUI>();
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.color = new Color(1f, 0.62f, 0.16f, 1f);
            text.enableAutoSizing = true;
            text.fontSizeMin = 8f;
            text.fontSizeMax = 18f;
            text.fontStyle = FontStyles.Bold;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            ApplyHudFont(text, Hud.instance != null ? Hud.instance.m_hoverName : null);

            Shadow shadow = labelObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.9f);
            shadow.effectDistance = new Vector2(1f, -1f);

            return new HoverFoodRowLabel(
                labelObject,
                rectTransform,
                text,
                labelObject.GetComponent<LayoutElement>());
        }

        internal void SetText(string text, float iconSize)
        {
            bool active = !string.IsNullOrWhiteSpace(text);
            if (_gameObject.activeSelf != active)
            {
                _gameObject.SetActive(active);
            }

            if (!active)
            {
                _lastText = string.Empty;
                _lastIconSize = -1f;
                return;
            }

            if (string.Equals(_lastText, text, StringComparison.Ordinal) &&
                Approximately(_lastIconSize, iconSize))
            {
                return;
            }

            float fontSize = Mathf.Clamp(iconSize * 0.34f, 8f, 18f);
            _text.fontSizeMax = fontSize;
            _text.fontSize = fontSize;
            _text.text = text;
            _text.ForceMeshUpdate();

            float preferredWidth = Mathf.Ceil(_text.preferredWidth);
            float preferredHeight = Mathf.Max(iconSize, Mathf.Ceil(_text.preferredHeight));
            _layoutElement.minWidth = preferredWidth;
            _layoutElement.preferredWidth = preferredWidth;
            _layoutElement.minHeight = preferredHeight;
            _layoutElement.preferredHeight = preferredHeight;
            _rectTransform.sizeDelta = new Vector2(preferredWidth, preferredHeight);
            _rectTransform.SetAsLastSibling();
            _lastText = text;
            _lastIconSize = iconSize;
        }
    }

    private sealed class HoverFoodIcon
    {
        private readonly Image _image;
        private readonly LayoutElement _layoutElement;
        private readonly TextMeshProUGUI _qualityText;
        private readonly TextMeshProUGUI _valueText;
        private Sprite? _lastSprite;
        private int? _lastRequiredQuality;
        private float _lastValue = float.NaN;
        private float _lastIconSize = -1f;

        private HoverFoodIcon(
            GameObject gameObject,
            RectTransform rectTransform,
            Image image,
            LayoutElement layoutElement,
            TextMeshProUGUI qualityText,
            TextMeshProUGUI valueText)
        {
            GameObject = gameObject;
            RectTransform = rectTransform;
            _image = image;
            _layoutElement = layoutElement;
            _qualityText = qualityText;
            _valueText = valueText;
        }

        internal GameObject GameObject { get; }
        internal RectTransform RectTransform { get; }

        internal static HoverFoodIcon Create(RectTransform parent, int index)
        {
            GameObject iconObject = new(
                $"FeedLikeGrandmaIcon{index}",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(LayoutElement));
            RectTransform iconTransform = iconObject.GetComponent<RectTransform>();
            iconTransform.SetParent(parent, false);

            Image image = iconObject.GetComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;

            LayoutElement layoutElement = iconObject.GetComponent<LayoutElement>();
            layoutElement.flexibleWidth = 0f;
            layoutElement.flexibleHeight = 0f;

            TextMeshProUGUI qualityText = CreateBadgeText(
                iconTransform,
                "Quality",
                new Vector2(0.45f, 0.55f),
                Vector2.one,
                TextAlignmentOptions.TopRight,
                Color.white);
            TextMeshProUGUI valueText = CreateBadgeText(
                iconTransform,
                "Value",
                Vector2.zero,
                new Vector2(1f, 0.45f),
                TextAlignmentOptions.BottomRight,
                new Color(1f, 0.82f, 0.18f, 1f));
            return new HoverFoodIcon(
                iconObject,
                iconTransform,
                image,
                layoutElement,
                qualityText,
                valueText);
        }

        internal void SetFood(ResolvedFoodEntry food, float iconSize)
        {
            if (ReferenceEquals(_lastSprite, food.Icon) &&
                _lastRequiredQuality == food.RequiredQuality &&
                Approximately(_lastValue, food.Value) &&
                Approximately(_lastIconSize, iconSize))
            {
                return;
            }

            _layoutElement.minWidth = iconSize;
            _layoutElement.minHeight = iconSize;
            _layoutElement.preferredWidth = iconSize;
            _layoutElement.preferredHeight = iconSize;
            RectTransform.sizeDelta = Vector2.one * iconSize;

            _image.sprite = food.Icon;
            _image.color = Color.white;

            float badgeMaxSize = Mathf.Clamp(iconSize * 0.34f, 8f, 18f);
            ConfigureBadgeSize(_qualityText, badgeMaxSize);
            ConfigureBadgeSize(_valueText, badgeMaxSize);

            _qualityText.gameObject.SetActive(food.RequiredQuality.HasValue);
            _qualityText.text = food.RequiredQuality.HasValue
                ? food.RequiredQuality.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            _valueText.text = FormatFoodValue(food.Value);
            _lastSprite = food.Icon;
            _lastRequiredQuality = food.RequiredQuality;
            _lastValue = food.Value;
            _lastIconSize = iconSize;
        }

        private static TextMeshProUGUI CreateBadgeText(
            RectTransform parent,
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            TextAlignmentOptions alignment,
            Color color)
        {
            GameObject textObject = new(
                $"FeedLikeGrandma{name}Badge",
                typeof(RectTransform),
                typeof(CanvasRenderer));
            textObject.SetActive(false);
            RectTransform rectTransform = textObject.GetComponent<RectTransform>();
            rectTransform.SetParent(parent, false);
            rectTransform.anchorMin = anchorMin;
            rectTransform.anchorMax = anchorMax;
            rectTransform.offsetMin = new Vector2(1f, 0f);
            rectTransform.offsetMax = new Vector2(-2f, 0f);

            TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
            text.alignment = alignment;
            text.color = color;
            text.enableAutoSizing = true;
            text.fontSizeMin = 6f;
            text.fontSizeMax = 18f;
            text.fontStyle = FontStyles.Bold;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            ApplyHudFont(text, Hud.instance != null ? Hud.instance.m_hoverName : null);

            Shadow shadow = textObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.9f);
            shadow.effectDistance = new Vector2(1f, -1f);
            textObject.SetActive(true);
            return text;
        }

        private static void ConfigureBadgeSize(TextMeshProUGUI text, float maxSize)
        {
            text.fontSizeMax = maxSize;
            text.fontSize = maxSize;
        }

        private static string FormatFoodValue(float value)
        {
            return Mathf.Abs(value - Mathf.Round(value)) < 0.001f
                ? Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
