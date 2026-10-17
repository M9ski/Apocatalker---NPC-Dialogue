using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ApocalypterTalk
{
    /// <summary>Runtime Canvas/TextMeshPro interface for the dialogue window.</summary>
    internal sealed class DialogueUI : IDisposable
    {
        readonly string pluginDirectory;
        readonly Action<PromptDef> onPrompt;
        readonly Action onClose;
        readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        readonly List<Image> scrollBackgroundImages = new List<Image>();
        readonly List<Image> scrollbarTrackImages = new List<Image>();
        readonly List<Image> scrollbarHandleImages = new List<Image>();

        GameObject root;
        GameObject eventSystemRoot;
        RectTransform panel;
        Image panelImage;
        TMP_Text title;
        TMP_Text hint;
        ScrollRect chatScroll;
        ScrollRect promptScroll;
        RectTransform chatContent;
        RectTransform promptContent;
        TMP_FontAsset font;
        Material outlineMaterial;
        List<PromptDef> currentChoices = new List<PromptDef>();
        string currentNpcName = "";
        float width = 600f;
        float height = 420f;
        float outline = 1f;
        bool customThemeEnabled;
        Color windowColor = new Color(0.145f, 0.145f, 0.145f, 0.95f);
        Color buttonColor = new Color(0.255f, 0.255f, 0.255f, 1f);
        Color textColor = new Color(0.957f, 0.957f, 0.957f, 1f);
        Color accentColor = new Color(0.655f, 0.655f, 0.655f, 1f);
        Color outlineColor = Color.black;
        RectOffset windowBorder = new RectOffset(16, 16, 16, 16);
        RectOffset windowPadding = new RectOffset(24, 24, 40, 24);
        RectOffset buttonBorder = new RectOffset(8, 8, 8, 8);
        bool disposed;

        public DialogueUI(string pluginDirectory, Action<PromptDef> onPrompt, Action onClose)
        {
            this.pluginDirectory = pluginDirectory;
            this.onPrompt = onPrompt;
            this.onClose = onClose;

            font = ResolveFont();
            if (font == null || font.material == null)
                throw new InvalidOperationException("Could not find a TextMeshPro font asset or create one from the built-in Arial font.");

            outlineMaterial = new Material(font.material);
            outlineMaterial.name = "ApocalypterTalk TMP Outline";
            outlineMaterial.hideFlags = HideFlags.HideAndDontSave;
            BuildCanvas();
            BuildWindow();
            BuildHint();
            EnsureEventSystem();
            root.SetActive(true);
            panel.gameObject.SetActive(false);
            hint.gameObject.SetActive(false);
        }

        static TMP_FontAsset ResolveFont()
        {
            TMP_FontAsset result = null;
            try { result = TMP_Settings.defaultFontAsset; }
            catch { }

            if (result != null) return result;

            TMP_FontAsset[] loaded = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            for (int i = 0; i < loaded.Length; i++)
                if (loaded[i] != null && loaded[i].material != null) return loaded[i];

            Font builtIn = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return builtIn != null ? TMP_FontAsset.CreateFontAsset(builtIn) : null;
        }

        void BuildCanvas()
        {
            root = new GameObject("ApocalypterTalk TMP Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(root);

            Canvas canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 32000;

            CanvasScaler scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
        }

        void BuildWindow()
        {
            panel = CreateRect("Dialogue Window", root.transform);
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 0.5f);

            panelImage = panel.gameObject.AddComponent<Image>();
            panelImage.color = new Color(0.26f, 0.19f, 0.14f, 0.96f);
            panelImage.raycastTarget = true;

            title = CreateText("NPC Name", panel, 20f, Color.white, TextAlignmentOptions.Center);
            RectTransform titleRect = title.rectTransform;
            titleRect.anchorMin = new Vector2(0.08f, 0.88f);
            titleRect.anchorMax = new Vector2(0.92f, 0.98f);
            titleRect.offsetMin = Vector2.zero;
            titleRect.offsetMax = Vector2.zero;

            chatScroll = CreateScrollArea("Chat Log", panel, out chatContent);
            SetAnchors((RectTransform)chatScroll.transform, new Vector2(0.04f, 0.40f), new Vector2(0.96f, 0.86f));

            promptScroll = CreateScrollArea("Prompt List", panel, out promptContent);
            SetAnchors((RectTransform)promptScroll.transform, new Vector2(0.04f, 0.05f), new Vector2(0.96f, 0.37f));
        }

        void BuildHint()
        {
            TMP_Text label = CreateText("Talk Prompt", root.transform, 24f, Color.white, TextAlignmentOptions.Center);
            hint = label;
            RectTransform rect = label.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 240f);
            rect.sizeDelta = new Vector2(700f, 48f);
        }

        ScrollRect CreateScrollArea(string name, Transform parentTransform, out RectTransform content)
        {
            RectTransform scrollRoot = CreateRect(name, parentTransform);
            Image scrollImage = scrollRoot.gameObject.AddComponent<Image>();
            scrollImage.color = new Color(0f, 0f, 0f, 0.035f);
            scrollImage.raycastTarget = true;
            scrollBackgroundImages.Add(scrollImage);

            ScrollRect scroll = scrollRoot.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = true;
            scroll.scrollSensitivity = 35f;

            RectTransform viewport = CreateRect("Viewport", scrollRoot);
            Stretch(viewport, 0f, 0f, 16f, 0f);
            // RectMask2D clips the children directly to the viewport rectangle. It avoids
            // stencil-mask material interactions that can hide TMP and button graphics in
            // this game's overlay Canvas.
            viewport.gameObject.AddComponent<RectMask2D>();

            content = CreateRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;

            VerticalLayoutGroup contentLayout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            contentLayout.childAlignment = TextAnchor.UpperLeft;
            contentLayout.childControlWidth = true;
            contentLayout.childControlHeight = true;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;
            contentLayout.spacing = 4f;
            contentLayout.padding = new RectOffset(2, 4, 2, 2);

            ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewport;
            scroll.content = content;
            scroll.verticalNormalizedPosition = 1f;

            RectTransform barRect = CreateRect("Scrollbar", scrollRoot);
            barRect.anchorMin = new Vector2(1f, 0f);
            barRect.anchorMax = new Vector2(1f, 1f);
            barRect.pivot = new Vector2(1f, 0.5f);
            barRect.anchoredPosition = Vector2.zero;
            barRect.sizeDelta = new Vector2(12f, 0f);
            Image barImage = barRect.gameObject.AddComponent<Image>();
            barImage.color = new Color(0.05f, 0.04f, 0.035f, 0.7f);
            scrollbarTrackImages.Add(barImage);

            RectTransform handleRect = CreateRect("Handle", barRect);
            Stretch(handleRect, 2f, 2f, 2f, 2f);
            Image handleImage = handleRect.gameObject.AddComponent<Image>();
            handleImage.color = new Color(0.68f, 0.61f, 0.50f, 0.95f);
            scrollbarHandleImages.Add(handleImage);

            Scrollbar scrollbar = barRect.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handleImage;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            scroll.verticalScrollbarSpacing = 2f;
            return scroll;
        }

        TMP_Text CreateText(string name, Transform parentTransform, float fontSize, Color color, TextAlignmentOptions alignment)
        {
            RectTransform rect = CreateRect(name, parentTransform);
            TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.fontSharedMaterial = outlineMaterial;
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = alignment;
            text.richText = true;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            text.margin = Vector4.zero;
            return text;
        }

        void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            eventSystemRoot = new GameObject("ApocalypterTalk EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            eventSystemRoot.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(eventSystemRoot);
        }

        static RectTransform CreateRect(string name, Transform parentTransform)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parentTransform, false);
            return (RectTransform)go.transform;
        }

        static void Stretch(RectTransform rect, float left, float bottom, float right, float top)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        static void SetAnchors(RectTransform rect, Vector2 min, Vector2 max)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        public void ApplyTheme(float width, float height, bool customThemeEnabled,
            string windowColorText, string buttonColorText, string textColorText, string accentColorText,
            string outlineColorText, string windowBorderText, string paddingText, string buttonBorderText, float outline)
        {
            this.width = width;
            this.height = height;
            this.outline = outline;
            this.customThemeEnabled = customThemeEnabled;
            windowColor = ParseColor(windowColorText, new Color(0.145f, 0.145f, 0.145f, 0.95f), "WindowColor");
            buttonColor = ParseColor(buttonColorText, new Color(0.255f, 0.255f, 0.255f, 1f), "ButtonColor");
            textColor = ParseColor(textColorText, new Color(0.957f, 0.957f, 0.957f, 1f), "TextColor");
            accentColor = ParseColor(accentColorText, new Color(0.655f, 0.655f, 0.655f, 1f), "AccentColor");
            outlineColor = ParseColor(outlineColorText, Color.black, "ChatTextOutlineColor");
            windowBorder = ParseOffset(windowBorderText, new RectOffset(16, 16, 16, 16));
            windowPadding = ParseOffset(paddingText, new RectOffset(24, 24, 40, 24));
            buttonBorder = ParseOffset(buttonBorderText, new RectOffset(8, 8, 8, 8));

            if (panel != null)
            {
                panel.sizeDelta = new Vector2(this.width, this.height);
                panel.anchoredPosition = new Vector2(0f, this.height * 0.5f + 100f - 540f);
                panelImage.sprite = this.customThemeEnabled ? LoadSprite("window.png", windowBorder) : null;
                panelImage.type = panelImage.sprite != null ? Image.Type.Sliced : Image.Type.Simple;
                panelImage.color = windowColor;
            }

            Color scrollBackground = windowColor;
            scrollBackground.a *= 0.10f;
            foreach (Image image in scrollBackgroundImages)
                if (image != null) image.color = scrollBackground;

            Color scrollbarTrack = windowColor;
            scrollbarTrack.a *= 0.65f;
            foreach (Image image in scrollbarTrackImages)
                if (image != null) image.color = scrollbarTrack;
            foreach (Image image in scrollbarHandleImages)
                if (image != null) image.color = accentColor;

            if (outlineMaterial != null)
            {
                if (outlineMaterial.HasProperty("_OutlineColor"))
                    outlineMaterial.SetColor("_OutlineColor", outlineColor);
                outlineMaterial.SetFloat("_OutlineWidth", Mathf.Clamp(this.outline * 0.36f, 0f, 0.65f));
                if (outlineMaterial.HasProperty("_FaceDilate"))
                    outlineMaterial.SetFloat("_FaceDilate", this.outline > 0f ? 0.12f : 0f);
                if (this.outline > 0f) outlineMaterial.EnableKeyword("OUTLINE_ON");
                else outlineMaterial.DisableKeyword("OUTLINE_ON");
            }

            if (root != null)
                foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
                    if (text != null) text.color = textColor;

            if (promptContent != null) RebuildChoices();
            Canvas.ForceUpdateCanvases();
        }

        static Color ParseColor(string value, Color fallback, string settingName)
        {
            Color parsed;
            if (ColorUtility.TryParseHtmlString((value ?? "").Trim(), out parsed)) return parsed;

            if (Plugin.Log != null)
                Plugin.Log.LogWarning("Theme: invalid HEX color for " + settingName + " ('" + value + "'); using the default color.");
            return fallback;
        }

        public void ShowPrompt(string npcName, KeyCode talkKey)
        {
            currentNpcName = npcName ?? "";
            hint.text = "Press <b>[" + talkKey + "]</b> to talk to " + currentNpcName;
            hint.gameObject.SetActive(true);
            panel.gameObject.SetActive(false);
        }

        public void HidePrompt()
        {
            if (hint != null) hint.gameObject.SetActive(false);
        }

        public void ShowDialogue(string npcName)
        {
            HidePrompt();
            currentNpcName = npcName ?? "";
            title.text = currentNpcName;
            ClearChildren(chatContent);
            currentChoices.Clear();
            RebuildChoices();
            panel.gameObject.SetActive(true);
            Canvas.ForceUpdateCanvases();
        }

        public void HideDialogue()
        {
            if (panel != null) panel.gameObject.SetActive(false);
        }

        public void AppendChatLine(string richText)
        {
            if (chatContent.childCount >= 60)
                UnityEngine.Object.Destroy(chatContent.GetChild(0).gameObject);
            TMP_Text text = CreateText("Chat Line", chatContent, 20f, textColor, TextAlignmentOptions.TopLeft);
            text.fontSharedMaterial = outlineMaterial;
            text.text = richText ?? "";
            RebuildScrollLayout(chatScroll, chatContent);
            ScrollChatToBottom();
        }

        public void ScrollChatToBottom()
        {
            Canvas.ForceUpdateCanvases();
            if (chatScroll != null) chatScroll.verticalNormalizedPosition = 0f;
        }

        public void SetChoices(List<PromptDef> choices)
        {
            currentChoices = choices != null ? new List<PromptDef>(choices) : new List<PromptDef>();
            RebuildChoices();
        }

        public string GetDiagnostics()
        {
            Canvas.ForceUpdateCanvases();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Apocatalker UI diagnostics]");
            sb.AppendLine("screen=" + Screen.width + "x" + Screen.height + " dpi=" + Screen.dpi +
                " timeScale=" + Time.timeScale.ToString("0.###"));
            Canvas canvas = root != null ? root.GetComponent<Canvas>() : null;
            CanvasScaler scaler = root != null ? root.GetComponent<CanvasScaler>() : null;
            sb.AppendLine("canvas=" + (canvas != null ? canvas.renderMode + " enabled=" + canvas.enabled + " sort=" + canvas.sortingOrder : "<missing>") +
                " scaleFactor=" + (canvas != null ? canvas.scaleFactor.ToString("0.###") : "n/a") +
                " referencePPU=" + (canvas != null ? canvas.referencePixelsPerUnit.ToString("0.###") : "n/a") +
                " scaler=" + (scaler != null ? scaler.uiScaleMode + " reference=" + scaler.referenceResolution + " match=" + scaler.matchWidthOrHeight.ToString("0.###") : "<missing>"));
            sb.AppendLine(DescribeRect("panel", panel));
            sb.AppendLine("panel image=" + (panelImage != null ? "enabled=" + panelImage.enabled + " type=" + panelImage.type +
                " sprite=" + (panelImage.sprite != null ? panelImage.sprite.name + " " + panelImage.sprite.rect.width + "x" + panelImage.sprite.rect.height + " ppu=" + panelImage.sprite.pixelsPerUnit : "<null>") : "<missing>"));
            sb.AppendLine(DescribeRect("title", title != null ? title.rectTransform : null) +
                " text='" + (title != null ? Short(title.text, 100) : "<missing>") + "' chars=" +
                (title != null && title.textInfo != null ? title.textInfo.characterCount.ToString() : "n/a") +
                " font=" + (font != null ? font.name : "<null>") + " material=" +
                (outlineMaterial != null && outlineMaterial.shader != null ? outlineMaterial.shader.name : "<null>") +
                " outlineColor=" + (outlineMaterial != null && outlineMaterial.HasProperty("_OutlineColor") ? outlineMaterial.GetColor("_OutlineColor").ToString() : "n/a") +
                " outlineWidth=" + (outlineMaterial != null && outlineMaterial.HasProperty("_OutlineWidth") ? outlineMaterial.GetFloat("_OutlineWidth").ToString("0.###") : "n/a") +
                " faceDilate=" + (outlineMaterial != null && outlineMaterial.HasProperty("_FaceDilate") ? outlineMaterial.GetFloat("_FaceDilate").ToString("0.###") : "n/a"));
            AppendScrollDiagnostics(sb, "chat", chatScroll, chatContent);
            AppendScrollDiagnostics(sb, "prompts", promptScroll, promptContent);
            sb.Append("choices=").Append(currentChoices != null ? currentChoices.Count : -1)
                .Append(" goodbyeIncluded=true").AppendLine();
            sb.AppendLine("[end Apocatalker UI diagnostics]");
            return sb.ToString();
        }

        static void AppendScrollDiagnostics(StringBuilder sb, string name, ScrollRect scroll, RectTransform content)
        {
            if (scroll == null)
            {
                sb.AppendLine(name + " scroll=<missing>");
                return;
            }
            sb.AppendLine(DescribeRect(name + " scroll", (RectTransform)scroll.transform) +
                " active=" + scroll.gameObject.activeInHierarchy + " enabled=" + scroll.enabled +
                " normalized=" + scroll.verticalNormalizedPosition.ToString("0.###"));
            sb.AppendLine(DescribeRect(name + " viewport", scroll.viewport) +
                " rectMask2D=" + (scroll.viewport != null && scroll.viewport.GetComponent<RectMask2D>() != null) +
                " stencilMask=" + (scroll.viewport != null && scroll.viewport.GetComponent<Mask>() != null));
            sb.AppendLine(DescribeRect(name + " content", content) +
                " children=" + (content != null ? content.childCount : -1) +
                " scrollbar=" + (scroll.verticalScrollbar != null ? "value=" + scroll.verticalScrollbar.value.ToString("0.###") + " handle=" + scroll.verticalScrollbar.size.ToString("0.###") : "<null>"));
            if (content == null) return;
            int limit = Math.Min(content.childCount, 8);
            for (int i = 0; i < limit; i++)
            {
                Transform child = content.GetChild(i);
                TMP_Text text = child.GetComponent<TMP_Text>();
                if (text == null) text = child.GetComponentInChildren<TMP_Text>(true);
                Image image = child.GetComponent<Image>();
                CanvasRenderer imageRenderer = image != null ? image.canvasRenderer : null;
                CanvasRenderer textRenderer = text != null ? text.canvasRenderer : null;
                sb.AppendLine("  item[" + i + "] " + DescribeRect(child.name, child as RectTransform) +
                    " active=" + child.gameObject.activeInHierarchy +
                    " image=" + (image != null ? image.enabled + "/" + image.color + "/type=" + image.type +
                        "/sprite=" + (image.sprite != null ? image.sprite.rect.width + "x" + image.sprite.rect.height + " ppu=" + image.sprite.pixelsPerUnit : "<null>") +
                        "/cull=" + (imageRenderer != null && imageRenderer.cull) : "<none>") +
                    " text='" +
                    (text != null ? Short(text.text, 100) : "<no TMP_Text>") + "' chars=" +
                    (text != null && text.textInfo != null ? text.textInfo.characterCount.ToString() : "n/a") +
                    " textEnabled=" + (text != null && text.enabled) +
                    " textColor=" + (text != null ? text.color.ToString() : "n/a") +
                    " textCull=" + (textRenderer != null && textRenderer.cull) +
                    " raycast=" + (text != null && text.raycastTarget));
            }
            if (content.childCount > limit) sb.AppendLine("  ... " + (content.childCount - limit) + " more item(s)");
        }

        static string DescribeRect(string name, RectTransform rect)
        {
            if (rect == null) return name + " rect=<missing>";
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return name + " rect=" + rect.rect.width.ToString("0.##") + "x" + rect.rect.height.ToString("0.##") +
                " anchored=" + rect.anchoredPosition + " anchors=" + rect.anchorMin + ".." + rect.anchorMax +
                " worldBL=" + corners[0] + "..TR=" + corners[2];
        }

        static string Short(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string singleLine = value.Replace("\r", " ").Replace("\n", " ");
            return singleLine.Length <= maxLength ? singleLine : singleLine.Substring(0, maxLength) + "…";
        }

        void RebuildChoices()
        {
            if (promptContent == null) return;
            ClearChildren(promptContent);
            for (int i = 0; i < currentChoices.Count; i++)
            {
                PromptDef prompt = currentChoices[i];
                CreateButton(prompt.Text, () => onPrompt(prompt));
            }
            CreateButton("Goodbye.", () => onClose());
            RebuildScrollLayout(promptScroll, promptContent);
            if (promptScroll != null)
            {
                promptScroll.verticalNormalizedPosition = 1f;
                promptScroll.Rebuild(CanvasUpdate.PostLayout);
            }
        }

        static void RebuildScrollLayout(ScrollRect scroll, RectTransform content)
        {
            if (content == null) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            if (scroll != null) scroll.Rebuild(CanvasUpdate.PostLayout);
        }

        void CreateButton(string textValue, Action clicked)
        {
            RectTransform buttonRect = CreateRect("Dialogue Button", promptContent);
            Image image = buttonRect.gameObject.AddComponent<Image>();
            Sprite normal = customThemeEnabled ? LoadSprite("button.png", buttonBorder) : null;
            image.sprite = normal;
            image.type = normal != null ? Image.Type.Sliced : Image.Type.Simple;
            image.color = buttonColor;

            Button button = buttonRect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => clicked());

            Sprite highlighted = customThemeEnabled ? LoadSprite("button_hover.png", buttonBorder) : null;
            Sprite pressed = customThemeEnabled ? LoadSprite("button_active.png", buttonBorder) : null;
            if (normal != null && highlighted != null)
            {
                button.transition = Selectable.Transition.SpriteSwap;
                button.spriteState = new SpriteState
                {
                    highlightedSprite = highlighted,
                    pressedSprite = pressed != null ? pressed : highlighted,
                    selectedSprite = highlighted
                };
            }
            else
            {
                button.transition = Selectable.Transition.ColorTint;
                button.colors = new ColorBlock
                {
                    normalColor = Color.white,
                    highlightedColor = Color.Lerp(Color.white, accentColor, 0.35f),
                    pressedColor = Color.Lerp(Color.white, accentColor, 0.60f),
                    selectedColor = Color.white,
                    disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f),
                    colorMultiplier = 1f,
                    fadeDuration = 0.1f
                };
            }

            VerticalLayoutGroup buttonLayout = buttonRect.gameObject.AddComponent<VerticalLayoutGroup>();
            buttonLayout.padding = new RectOffset(14, 14, 6, 6);
            buttonLayout.childAlignment = TextAnchor.MiddleLeft;
            buttonLayout.childControlWidth = true;
            buttonLayout.childControlHeight = true;
            buttonLayout.childForceExpandWidth = true;
            buttonLayout.childForceExpandHeight = false;

            TMP_Text label = CreateText("Label", buttonRect, 20f, textColor, TextAlignmentOptions.MidlineLeft);
            label.text = textValue ?? "";
            LayoutElement buttonSize = buttonRect.gameObject.AddComponent<LayoutElement>();
            buttonSize.minHeight = 40f;
            float preferredTextHeight = label.GetPreferredValues(label.text,
                Mathf.Max(120f, width - windowPadding.horizontal - 48f), 0f).y;
            buttonSize.preferredHeight = Mathf.Max(42f, preferredTextHeight + 14f);
            buttonSize.flexibleHeight = 0f;
        }

        Sprite LoadSprite(string fileName, RectOffset border)
        {
            string key = fileName + "|" + border.left + "," + border.right + "," + border.top + "," + border.bottom;
            Sprite cached;
            if (sprites.TryGetValue(key, out cached)) return cached;

            Texture2D texture = LoadTexture(fileName);
            if (texture == null) return null;

            Vector4 slice = new Vector4(border.left, border.bottom, border.right, border.top);
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, slice);
            sprite.name = "ApocalypterTalk " + fileName;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            sprites[key] = sprite;
            return sprite;
        }

        Texture2D LoadTexture(string fileName)
        {
            Texture2D cached;
            if (textures.TryGetValue(fileName, out cached)) return cached;

            Texture2D result = null;
            try
            {
                string path = Path.Combine(Path.Combine(pluginDirectory, "theme"), fileName);
                if (File.Exists(path))
                {
                    result = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                    if (!result.LoadImage(File.ReadAllBytes(path), true))
                    {
                        UnityEngine.Object.Destroy(result);
                        result = null;
                    }
                    else
                    {
                        result.filterMode = FilterMode.Bilinear;
                        result.wrapMode = TextureWrapMode.Clamp;
                        result.hideFlags = HideFlags.HideAndDontSave;
                    }
                }
            }
            catch { result = null; }

            textures[fileName] = result;
            return result;
        }

        static RectOffset ParseOffset(string text, RectOffset fallback)
        {
            string[] parts = (text ?? "").Split(',');
            if (parts.Length == 1)
            {
                int one;
                if (int.TryParse(parts[0].Trim(), out one)) return new RectOffset(one, one, one, one);
                return fallback;
            }
            if (parts.Length == 4)
            {
                int[] values = new int[4];
                for (int i = 0; i < 4; i++)
                    if (!int.TryParse(parts[i].Trim(), out values[i])) return fallback;
                return new RectOffset(values[0], values[1], values[2], values[3]);
            }
            return fallback;
        }

        static void ClearChildren(Transform parentTransform)
        {
            for (int i = parentTransform.childCount - 1; i >= 0; i--)
            {
                GameObject child = parentTransform.GetChild(i).gameObject;
                // Destroy is deferred until the end of the frame. Disable immediately so
                // old controls cannot remain visible or participate in layout during a rebuild.
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (root != null) UnityEngine.Object.Destroy(root);
            if (eventSystemRoot != null) UnityEngine.Object.Destroy(eventSystemRoot);
            foreach (var sprite in sprites.Values)
                if (sprite != null) UnityEngine.Object.Destroy(sprite);
            foreach (var texture in textures.Values)
                if (texture != null) UnityEngine.Object.Destroy(texture);
            if (outlineMaterial != null) UnityEngine.Object.Destroy(outlineMaterial);
        }
    }
}
