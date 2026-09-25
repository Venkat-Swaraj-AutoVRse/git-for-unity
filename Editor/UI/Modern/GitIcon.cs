using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VersionControl.Git.UI
{
    /// <summary>
    /// A small icon from <c>Editor/UI/Modern/Icons</c>. The PNGs are white on transparent and are
    /// tinted with the element's resolved USS <c>color</c>, so icons follow text colour, hover and
    /// theme rules. Regenerate the PNGs with <c>Tools~/generate_icons.py</c>.
    /// </summary>
    class GitIcon : VisualElement
    {
        public const string UssClassName = "gfu-icon";
        private static string IconFolder => GitUi.PackageRoot + "/Editor/UI/Modern/Icons/";

        private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();

        private string iconName;

        public GitIcon(string name, int size = 16)
        {
            AddToClassList(UssClassName);
            pickingMode = PickingMode.Ignore;
            style.width = size;
            style.height = size;
            style.flexShrink = 0;
            style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            SetIcon(name);
            // Fires whenever this element's style is re-resolved (class, hover, theme), which is when
            // its colour can change. The .gfu-icon rule declares a custom property so it always fires.
            RegisterCallback<CustomStyleResolvedEvent>(_ => ApplyTint());
        }

        public string IconName
        {
            get => iconName;
            set
            {
                if (iconName != value)
                    SetIcon(value);
            }
        }

        private void SetIcon(string name)
        {
            iconName = name;
            var texture = Load(name);
            style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
        }

        private void ApplyTint()
        {
            style.unityBackgroundImageTintColor = resolvedStyle.color;
        }

        private static Texture2D Load(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            // a cached null means "known missing": warn once, not for every row that asks
            if (Cache.TryGetValue(name, out var texture) && (texture != null || ReferenceEquals(texture, null)))
                return texture;
            texture = AssetDatabase.LoadAssetAtPath<Texture2D>(IconFolder + name + ".png");
            if (texture == null)
            {
                Debug.LogWarning("[Git] Missing icon '" + name + "' in " + IconFolder);
                texture = null;
            }
            Cache[name] = texture;
            return texture;
        }
    }
}
