using System.Collections.Generic;
using UnityEditor;
using UnityEditor.PackageManager;

namespace OpenIDEA.Unity
{
    /// Per-user preferences, kept in EditorPrefs like the other code editor packages.
    internal static class OpenIdeaSettings
    {
        const string Prefix = "openidea_";

        public static readonly KeyValuePair<PackageSource, string>[] PackageSources =
        {
            new KeyValuePair<PackageSource, string>(PackageSource.Embedded, "Embedded packages"),
            new KeyValuePair<PackageSource, string>(PackageSource.Local, "Local packages"),
            new KeyValuePair<PackageSource, string>(PackageSource.Registry, "Registry packages"),
            new KeyValuePair<PackageSource, string>(PackageSource.Git, "Git packages"),
            new KeyValuePair<PackageSource, string>(PackageSource.BuiltIn, "Built-in packages"),
            new KeyValuePair<PackageSource, string>(PackageSource.LocalTarball, "Local tarball"),
            new KeyValuePair<PackageSource, string>(PackageSource.Unknown, "Packages from unknown sources"),
        };

        public static bool GeneratesFor(PackageSource source) =>
            EditorPrefs.GetBool(Prefix + "generate_" + source, source == PackageSource.Embedded || source == PackageSource.Local);

        public static void SetGeneratesFor(PackageSource source, bool value)
        {
            EditorPrefs.SetBool(Prefix + "generate_" + source, value);
            ProjectGeneration.Sync(true);
        }

        public static bool RefreshOnSave
        {
            get => EditorPrefs.GetBool(Prefix + "refresh_on_save", true);
            set => EditorPrefs.SetBool(Prefix + "refresh_on_save", value);
        }
    }
}
