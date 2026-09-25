using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.VersionControl.Git.UI
{
    /// <summary>Small factory helpers so the panels read as layout rather than plumbing.</summary>
    static class GitUi
    {
        private static string packageRoot;

        /// <summary>
        /// Asset path of this package's root (e.g. "Packages/com.ruthless.unitygit.ui"), looked up from
        /// the assembly instead of hardcoded, so renaming or embedding the package keeps the
        /// window's stylesheet and icons loading.
        /// </summary>
        public static string PackageRoot => packageRoot ?? (packageRoot = FindPackageRoot());

        private static string FindPackageRoot()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(GitUi).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.assetPath))
                return info.assetPath;

            // not installed as a package (e.g. copied under Assets/): find the stylesheet next to this code
            const string modernFolder = "/Editor/UI/Modern/GitWindow.uss";
            foreach (var guid in AssetDatabase.FindAssets("GitWindow t:StyleSheet"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(modernFolder, StringComparison.OrdinalIgnoreCase))
                    return path.Substring(0, path.Length - modernFolder.Length);
            }
            return "Packages/com.ruthless.unitygit.ui";
        }

        public static VisualElement Row(string className = null, params VisualElement[] children)
        {
            var e = new VisualElement();
            e.AddToClassList("gfu-row");
            if (!string.IsNullOrEmpty(className))
                AddClasses(e, className);
            foreach (var c in children)
                if (c != null)
                    e.Add(c);
            return e;
        }

        public static VisualElement Column(string className = null, params VisualElement[] children)
        {
            var e = new VisualElement();
            e.AddToClassList("gfu-col");
            if (!string.IsNullOrEmpty(className))
                AddClasses(e, className);
            foreach (var c in children)
                if (c != null)
                    e.Add(c);
            return e;
        }

        public static VisualElement Div(string className)
        {
            var e = new VisualElement();
            AddClasses(e, className);
            return e;
        }

        public static VisualElement Spacer()
        {
            var e = new VisualElement();
            e.AddToClassList("gfu-spacer");
            return e;
        }

        public static Label Text(string text, string className = null)
        {
            var l = new Label(text);
            if (!string.IsNullOrEmpty(className))
                AddClasses(l, className);
            return l;
        }

        public static Button Button(string text, Action onClick, string kind = "secondary", string icon = null, string tooltip = null)
        {
            var b = new Button(onClick);
            b.AddToClassList("gfu-btn");
            b.AddToClassList("gfu-btn--" + kind);
            if (!string.IsNullOrEmpty(icon))
                b.Add(new GitIcon(icon, 14));
            if (!string.IsNullOrEmpty(text))
            {
                var l = new Label(text);
                l.AddToClassList("gfu-btn__label");
                b.Add(l);
            }
            else
            {
                b.AddToClassList("gfu-btn--icon-only");
            }
            if (!string.IsNullOrEmpty(tooltip))
                b.tooltip = tooltip;
            return b;
        }

        public static void SetButtonText(Button button, string text)
        {
            var label = button.Q<Label>(className: "gfu-btn__label");
            if (label != null)
                label.text = text;
        }

        public static Label Pill(string text, string tone)
        {
            var l = new Label(text);
            l.AddToClassList("gfu-pill");
            l.AddToClassList("gfu-pill--" + tone);
            return l;
        }

        public static void SetPill(Label pill, string text, string tone)
        {
            pill.text = text;
            foreach (var t in Tones)
                pill.EnableInClassList("gfu-pill--" + t, t == tone);
        }

        private static readonly string[] Tones = { "edited", "new", "deleted", "moved", "info", "warn", "good", "muted" };

        public static VisualElement Banner(string tone, string title, string body, params VisualElement[] actions)
        {
            var b = new VisualElement();
            b.AddToClassList("gfu-banner");
            b.AddToClassList("gfu-banner--" + tone);
            var icon = new GitIcon(tone == "error" ? "error" : tone == "warn" ? "warn" : "info", 16);
            icon.AddToClassList("gfu-banner__icon");
            b.Add(icon);
            var col = Column("gfu-banner__content");
            var t = Text(title, "gfu-banner__title");
            t.name = "title";
            col.Add(t);
            if (!string.IsNullOrEmpty(body))
            {
                var bd = Text(body, "gfu-banner__body");
                bd.name = "body";
                col.Add(bd);
            }
            if (actions != null && actions.Length > 0)
            {
                var row = Row("gfu-banner__actions");
                foreach (var a in actions)
                    row.Add(a);
                col.Add(row);
            }
            b.Add(col);
            return b;
        }

        public static Label SectionLabel(string text)
        {
            return Text(text, "gfu-section-label");
        }

        public static TextField Field(string placeholder, bool multiline = false)
        {
            var f = new TextField { multiline = multiline };
            f.AddToClassList("gfu-field");
            if (multiline)
                f.AddToClassList("gfu-field--multiline");
#if UNITY_2023_1_OR_NEWER
            f.textEdition.placeholder = placeholder;
            f.textEdition.hidePlaceholderOnFocus = true;
#else
            AddPlaceholder(f, placeholder);
#endif
            return f;
        }

#if !UNITY_2023_1_OR_NEWER
        /// <summary>
        /// TextField placeholders only exist from Unity 2023.1, so older editors get a hint label
        /// laid over the input, hidden while the field has focus or any text.
        /// </summary>
        private static void AddPlaceholder(TextField field, string placeholder)
        {
            if (string.IsNullOrEmpty(placeholder))
                return;
            var input = field.Q(className: TextField.inputUssClassName);
            if (input == null)
                return;

            var hint = new Label(placeholder) { pickingMode = PickingMode.Ignore };
            hint.AddToClassList("gfu-field__placeholder");
            input.Add(hint);

            var focused = false;
            void Update() => hint.style.display = !focused && string.IsNullOrEmpty(field.value) ? DisplayStyle.Flex : DisplayStyle.None;

            field.RegisterValueChangedCallback(_ => Update());
            field.RegisterCallback<FocusInEvent>(_ => { focused = true; Update(); });
            field.RegisterCallback<FocusOutEvent>(_ => { focused = false; Update(); });
            // SetValueWithoutNotify raises no event, so also catch values set from code
            field.schedule.Execute(Update).Every(250);
            Update();
        }
#endif

        public static VisualElement Avatar(string name, int size = 22)
        {
            var e = new Label(Initials(name));
            e.AddToClassList("gfu-avatar");
            e.style.width = size;
            e.style.height = size;
            e.style.borderTopLeftRadius = e.style.borderTopRightRadius = e.style.borderBottomLeftRadius = e.style.borderBottomRightRadius = size / 2f;
            e.style.backgroundColor = AvatarColor(name);
            e.style.fontSize = size < 20 ? 8 : 10;
            return e;
        }

        public static void SetAvatar(Label avatar, string name)
        {
            avatar.text = Initials(name);
            avatar.style.backgroundColor = AvatarColor(name);
        }

        public static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "?";
            var parts = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
                return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
            return (parts[0].Substring(0, 1) + parts[parts.Length - 1].Substring(0, 1)).ToUpperInvariant();
        }

        private static readonly Color[] AvatarPalette =
        {
            new Color32(0xE8, 0xB8, 0x6A, 0xFF), new Color32(0x9A, 0xD0, 0xA0, 0xFF), new Color32(0x9F, 0xC3, 0xF0, 0xFF),
            new Color32(0xD9, 0xA0, 0xE0, 0xFF), new Color32(0xF0, 0xA0, 0x98, 0xFF), new Color32(0x9E, 0xD9, 0xD4, 0xFF),
        };

        private static Color AvatarColor(string name)
        {
            if (string.IsNullOrEmpty(name))
                return AvatarPalette[0];
            var hash = 0;
            foreach (var ch in name.ToLowerInvariant())
                hash = hash * 31 + ch;
            return AvatarPalette[Math.Abs(hash % AvatarPalette.Length)];
        }

        private static void AddClasses(VisualElement e, string classes)
        {
            foreach (var c in classes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                e.AddToClassList(c);
        }

        // ---- formatting ----

        public static string Plural(int count, string singular, string plural = null)
        {
            return count + " " + (count == 1 ? singular : plural ?? singular + "s");
        }

        public static string DayLabel(DateTimeOffset time)
        {
            var local = time.ToLocalTime().Date;
            var today = DateTime.Now.Date;
            if (local == today)
                return "Today";
            if (local == today.AddDays(-1))
                return "Yesterday";
            if (local.Year == today.Year)
                return local.ToString("d MMM", CultureInfo.CurrentCulture);
            return local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        }

        public static string TimeLabel(DateTimeOffset time)
        {
            var local = time.ToLocalTime();
            var day = DayLabel(time);
            var clock = local.ToString("HH:mm", CultureInfo.CurrentCulture);
            return day == "Today" ? clock : day + " " + clock;
        }

        public static string Since(DateTimeOffset time)
        {
            if (time == DateTimeOffset.MinValue)
                return string.Empty;
            var day = DayLabel(time);
            var clock = time.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
            return day == "Today" ? "since " + clock + " today" : "since " + day + " " + clock;
        }

        public static string Ago(DateTime? time)
        {
            if (!time.HasValue)
                return null;
            var span = DateTime.Now - time.Value;
            if (span.TotalSeconds < 60)
                return "just now";
            if (span.TotalMinutes < 60)
                return Plural((int)span.TotalMinutes, "min") + " ago";
            if (span.TotalHours < 24)
                return Plural((int)span.TotalHours, "hour") + " ago";
            return time.Value.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
        }

        public static string FileSize(long bytes)
        {
            if (bytes < 0)
                return null;
            if (bytes < 1024)
                return bytes + " B";
            if (bytes < 1024 * 1024)
                return (bytes / 1024f).ToString("0", CultureInfo.CurrentCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024)
                return (bytes / (1024f * 1024f)).ToString("0.0", CultureInfo.CurrentCulture) + " MB";
            return (bytes / (1024f * 1024f * 1024f)).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
        }

        public static void Ping(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return;
            var obj = AssetDatabase.LoadMainAssetAtPath(projectPath);
            if (obj != null)
                EditorGUIUtility.PingObject(obj);
        }

        public static void Select(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return;
            var obj = AssetDatabase.LoadMainAssetAtPath(projectPath);
            if (obj != null)
                Selection.activeObject = obj;
        }

        public static string WebUrl(string remoteUrl)
        {
            if (string.IsNullOrEmpty(remoteUrl))
                return null;
            var url = remoteUrl.Trim();
            if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            {
                // git@github.com:owner/repo.git -> https://github.com/owner/repo
                var at = url.IndexOf('@');
                var colon = url.IndexOf(':', at + 1);
                if (colon > at)
                    url = "https://" + url.Substring(at + 1, colon - at - 1) + "/" + url.Substring(colon + 1);
            }
            else if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url.Substring(6).Substring(url.Substring(6).IndexOf('@') + 1);
            }
            if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                url = url.Substring(0, url.Length - 4);
            return url;
        }

        public static string ShortRemote(string remoteUrl)
        {
            var web = WebUrl(remoteUrl);
            if (string.IsNullOrEmpty(web))
                return null;
            var idx = web.IndexOf("://", StringComparison.Ordinal);
            return idx >= 0 ? web.Substring(idx + 3) : web;
        }
    }

    enum AssetKind
    {
        Scene,
        Prefab,
        Material,
        Texture,
        Model,
        Animation,
        Audio,
        Script,
        Shader,
        Settings,
        Folder,
        Other,
    }

    /// <summary>Groups files the way artists think about them and finds a fitting icon.</summary>
    static class AssetKinds
    {
        private static readonly Dictionary<string, AssetKind> ByExtension = new Dictionary<string, AssetKind>(StringComparer.OrdinalIgnoreCase)
        {
            [".unity"] = AssetKind.Scene,
            [".prefab"] = AssetKind.Prefab,
            [".mat"] = AssetKind.Material, [".physicmaterial"] = AssetKind.Material, [".physicsmaterial"] = AssetKind.Material,
            [".png"] = AssetKind.Texture, [".jpg"] = AssetKind.Texture, [".jpeg"] = AssetKind.Texture, [".tga"] = AssetKind.Texture,
            [".psd"] = AssetKind.Texture, [".exr"] = AssetKind.Texture, [".hdr"] = AssetKind.Texture, [".tif"] = AssetKind.Texture,
            [".tiff"] = AssetKind.Texture, [".bmp"] = AssetKind.Texture, [".gif"] = AssetKind.Texture, [".renderTexture"] = AssetKind.Texture,
            [".cubemap"] = AssetKind.Texture, [".spriteatlas"] = AssetKind.Texture, [".spriteatlasv2"] = AssetKind.Texture,
            [".fbx"] = AssetKind.Model, [".obj"] = AssetKind.Model, [".blend"] = AssetKind.Model, [".dae"] = AssetKind.Model,
            [".3ds"] = AssetKind.Model, [".max"] = AssetKind.Model, [".ma"] = AssetKind.Model, [".mb"] = AssetKind.Model,
            [".glb"] = AssetKind.Model, [".gltf"] = AssetKind.Model, [".mesh"] = AssetKind.Model, [".asset"] = AssetKind.Other,
            [".anim"] = AssetKind.Animation, [".controller"] = AssetKind.Animation, [".overrideController"] = AssetKind.Animation,
            [".mask"] = AssetKind.Animation, [".playable"] = AssetKind.Animation, [".signal"] = AssetKind.Animation,
            [".wav"] = AssetKind.Audio, [".mp3"] = AssetKind.Audio, [".ogg"] = AssetKind.Audio, [".aif"] = AssetKind.Audio,
            [".aiff"] = AssetKind.Audio, [".flac"] = AssetKind.Audio, [".mixer"] = AssetKind.Audio,
            [".cs"] = AssetKind.Script, [".asmdef"] = AssetKind.Script, [".asmref"] = AssetKind.Script, [".js"] = AssetKind.Script,
            [".json"] = AssetKind.Script, [".uxml"] = AssetKind.Script, [".uss"] = AssetKind.Script,
            [".shader"] = AssetKind.Shader, [".shadergraph"] = AssetKind.Shader, [".shadersubgraph"] = AssetKind.Shader,
            [".hlsl"] = AssetKind.Shader, [".cginc"] = AssetKind.Shader, [".compute"] = AssetKind.Shader, [".vfx"] = AssetKind.Shader,
        };

        public static AssetKind Classify(string path, bool isFolder = false)
        {
            if (isFolder)
                return AssetKind.Folder;
            if (string.IsNullOrEmpty(path))
                return AssetKind.Other;
            var normalized = GitSession.Normalize(path);
            if (normalized.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/manifest.json", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/packages-lock.json", StringComparison.OrdinalIgnoreCase))
                return AssetKind.Settings;
            var ext = Path.GetExtension(normalized);
            return ByExtension.TryGetValue(ext, out var kind) ? kind : AssetKind.Other;
        }

        public static string GroupLabel(AssetKind kind)
        {
            switch (kind)
            {
                case AssetKind.Scene: return "Scenes";
                case AssetKind.Prefab: return "Prefabs";
                case AssetKind.Material: return "Materials";
                case AssetKind.Texture: return "Textures";
                case AssetKind.Model: return "Models";
                case AssetKind.Animation: return "Animation";
                case AssetKind.Audio: return "Audio";
                case AssetKind.Script: return "Scripts & data";
                case AssetKind.Shader: return "Shaders & VFX";
                case AssetKind.Settings: return "Project settings";
                case AssetKind.Folder: return "Folders";
                default: return "Other files";
            }
        }

        /// <summary>Binary formats that git cannot merge, so locking them is worthwhile.</summary>
        public static bool IsUnmergeable(AssetKind kind)
        {
            return kind == AssetKind.Scene || kind == AssetKind.Prefab || kind == AssetKind.Texture || kind == AssetKind.Model || kind == AssetKind.Audio;
        }

        private static readonly Dictionary<AssetKind, Texture> FallbackIcons = new Dictionary<AssetKind, Texture>();

        public static Texture Icon(string projectPath, AssetKind kind)
        {
            if (!string.IsNullOrEmpty(projectPath))
            {
                var cached = AssetDatabase.GetCachedIcon(projectPath);
                if (cached != null)
                    return cached;
            }

            if (FallbackIcons.TryGetValue(kind, out var tex) && tex != null)
                return tex;
            tex = LoadBuiltin(FallbackIconName(kind));
            FallbackIcons[kind] = tex;
            return tex;
        }

        private static string FallbackIconName(AssetKind kind)
        {
            switch (kind)
            {
                case AssetKind.Scene: return "SceneAsset Icon";
                case AssetKind.Prefab: return "Prefab Icon";
                case AssetKind.Material: return "Material Icon";
                case AssetKind.Texture: return "Texture Icon";
                case AssetKind.Model: return "PrefabModel Icon";
                case AssetKind.Animation: return "AnimationClip Icon";
                case AssetKind.Audio: return "AudioClip Icon";
                case AssetKind.Script: return "cs Script Icon";
                case AssetKind.Shader: return "Shader Icon";
                case AssetKind.Folder: return "Folder Icon";
                case AssetKind.Settings: return "Settings";
                default: return "DefaultAsset Icon";
            }
        }

        private static Texture LoadBuiltin(string name)
        {
            try
            {
                var content = EditorGUIUtility.IconContent(name);
                return content?.image;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// One row in the Changes list: an asset and its .meta file, folded together so artists
    /// never have to think about .meta files.
    /// </summary>
    class ChangeEntry
    {
        public string Key;              // repository-relative asset path (without .meta)
        public string DisplayName;
        public string Directory;
        public string ProjectPath;
        public AssetKind Kind;
        public GitStatusEntry? Asset;
        public GitStatusEntry? Meta;
        public GitFileStatus Status;
        public bool SettingsOnly;       // only the .meta changed
        public bool IsConflict;
        public GitLock? Lock;
        public bool LockedByMe;
        public string LockOwner;
        public long SizeBytes = -1;

        public bool LockedByOther => Lock.HasValue && !LockedByMe;

        public IEnumerable<GitStatusEntry> Entries
        {
            get
            {
                if (Asset.HasValue)
                    yield return Asset.Value;
                if (Meta.HasValue)
                    yield return Meta.Value;
            }
        }

        public IEnumerable<string> Paths => Entries.Select(e => e.Path);

        public string StatusLabel
        {
            get
            {
                if (IsConflict)
                    return "Conflict";
                if (SettingsOnly)
                    return "Settings";
                switch (Status)
                {
                    case GitFileStatus.Added:
                    case GitFileStatus.Untracked:
                    case GitFileStatus.Copied:
                        return "New";
                    case GitFileStatus.Deleted:
                        return "Deleted";
                    case GitFileStatus.Renamed:
                        return "Moved";
                    default:
                        return "Edited";
                }
            }
        }

        public string StatusTone
        {
            get
            {
                if (IsConflict)
                    return "deleted";
                if (SettingsOnly)
                    return "info";
                switch (Status)
                {
                    case GitFileStatus.Added:
                    case GitFileStatus.Untracked:
                    case GitFileStatus.Copied:
                        return "new";
                    case GitFileStatus.Deleted:
                        return "deleted";
                    case GitFileStatus.Renamed:
                        return "moved";
                    default:
                        return "edited";
                }
            }
        }

        public static List<ChangeEntry> Build(GitSession session)
        {
            var map = new Dictionary<string, ChangeEntry>(StringComparer.OrdinalIgnoreCase);
            var repoPath = session.RepositoryPath;

            foreach (var entry in session.Changes)
            {
                var path = GitSession.Normalize(entry.Path);
                var isMeta = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
                var key = isMeta ? path.Substring(0, path.Length - 5) : path;

                if (!map.TryGetValue(key, out var change))
                {
                    change = new ChangeEntry { Key = key };
                    map[key] = change;
                }

                if (isMeta)
                    change.Meta = entry;
                else
                    change.Asset = entry;

                if (entry.Status == GitFileStatus.Unmerged || entry.Unmerged)
                    change.IsConflict = true;
            }

            var result = new List<ChangeEntry>(map.Count);
            foreach (var change in map.Values)
            {
                var key = change.Key;
                var main = change.Asset ?? change.Meta.Value;
                change.SettingsOnly = !change.Asset.HasValue;
                change.Status = main.Status;
                change.DisplayName = Path.GetFileName(key);
                var dir = Path.GetDirectoryName(key);
                change.Directory = string.IsNullOrEmpty(dir) ? string.Empty : GitSession.Normalize(dir);
                change.ProjectPath = session.ToAssetPath(key);

                var isFolder = false;
                if (!string.IsNullOrEmpty(repoPath))
                {
                    try
                    {
                        var full = Path.Combine(repoPath, key);
                        if (System.IO.Directory.Exists(full))
                            isFolder = true;
                        else if (change.Asset.HasValue && File.Exists(full))
                            change.SizeBytes = new FileInfo(full).Length;
                    }
                    catch (Exception)
                    {
                    }
                }
                change.Kind = AssetKinds.Classify(key, isFolder);

                var gitLock = session.FindLock(key);
                if (gitLock.HasValue)
                {
                    change.Lock = gitLock;
                    change.LockedByMe = session.IsMine(gitLock.Value);
                    change.LockOwner = gitLock.Value.Owner.Name;
                }
                result.Add(change);
            }

            result.Sort((a, b) =>
            {
                var k = a.Kind.CompareTo(b.Kind);
                return k != 0 ? k : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }
    }
}
