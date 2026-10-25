#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Completes a player's CEF payload after Unity builds it.
/// Windows: copies what Unity does not ship from Assets/Plugins/x86_64 (the helper, CEF's resources, locales and
/// license files, the bridge's notices) and marks the helper a windowed program, so CEF's child processes open no consoles.
/// macOS: assembles the payload NativePlugin/build-cef-mac.sh leaves in NativePlugin/mac: the bridge bundle into
/// Contents/PlugIns, the framework and one helper app per CEF subprocess kind into Contents/Frameworks, and the
/// license files into Contents/Resources/CEF.
/// </summary>
public sealed class CefPlayerPayload : IPostprocessBuildWithReport
{
    private const string PluginFolder = "Plugins/x86_64";
    private const string HelperFileName = "UnityCefHelper.exe";
    private const string NoticesFileName = "UnityCefPlugin.NOTICES.txt";
    public const string MacFramework = "Chromium Embedded Framework.framework";

    // IMAGE_SUBSYSTEM_* values and the PE layout that locates the field.
    private const ushort WindowsGui = 2;
    private const ushort WindowsCui = 3;
    private const int NewHeaderOffsetField = 0x3C;
    private const int CoffHeaderSize = 24;
    private const int SubsystemOffsetInOptionalHeader = 68;

    // CEF launches each subprocess kind from its own helper app beside the base one.
    private static readonly string[] MacHelperSuffixes = { "", " (GPU)", " (Renderer)", " (Plugin)", " (Alerts)" };

    public static string MacPayloadDirectory => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "NativePlugin", "mac"));

    public int callbackOrder => 0;

    public void OnPostprocessBuild(BuildReport report)
    {
        if (report.summary.platform == BuildTarget.StandaloneWindows64) CompleteWindows(report.summary.outputPath);
        else if (report.summary.platform == BuildTarget.StandaloneOSX) CompleteMac(report.summary.outputPath);
    }

    private static void CompleteWindows(string playerPath)
    {
        string source = Path.Combine(Application.dataPath, PluginFolder);
        if (!File.Exists(Path.Combine(source, "libcef.dll")))
        {
            throw new BuildFailedException("The CEF runtime is missing at " + source + " - run NativePlugin/fetch-cef-windows.ps1 before building.");
        }

        string destination = Path.Combine(Path.GetDirectoryName(playerPath), Path.GetFileNameWithoutExtension(playerPath) + "_Data", PluginFolder);
        int copied = CopyTree(source, destination);
        Debug.Log("CefPlayerPayload: completed " + destination + " with " + copied + " file(s) Unity did not ship.");

        string helper = Path.Combine(destination, HelperFileName);
        if (!File.Exists(helper)) throw new BuildFailedException("The CEF helper is missing at " + helper + ".");
        if (MakeWindowed(helper)) Debug.Log("CefPlayerPayload: marked " + HelperFileName + " a windowed program so CEF's children open no consoles.");
    }

    private static void CompleteMac(string app)
    {
        string source = MacPayloadDirectory;
        if (!Directory.Exists(Path.Combine(source, "UnityCefPlugin.bundle")) || !Directory.Exists(Path.Combine(source, MacFramework)))
        {
            Debug.LogWarning("CefPlayerPayload: no Mac CEF payload at " + source + "; this Mac player has no HTML UI. Run NativePlugin/build-cef-mac.sh on an Apple Silicon Mac.");
            return;
        }

        string contents = Path.Combine(app, "Contents");
        string frameworks = Path.Combine(contents, "Frameworks");
        string notices = Path.Combine(contents, "Resources", "CEF");
        int copied = CopyTree(Path.Combine(source, "UnityCefPlugin.bundle"), Path.Combine(contents, "PlugIns", "UnityCefPlugin.bundle"));
        copied += CopyTree(Path.Combine(source, MacFramework), Path.Combine(frameworks, MacFramework));
        Directory.CreateDirectory(notices);
        foreach (string notice in new[] { "LICENSE.txt", "CREDITS.html" })
        {
            if (CopyIfDifferent(Path.Combine(source, notice), Path.Combine(notices, notice))) copied++;
        }
        if (CopyIfDifferent(Path.Combine(Application.dataPath, PluginFolder, NoticesFileName), Path.Combine(notices, NoticesFileName))) copied++;

        string identifier = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Standalone);
        foreach (string suffix in MacHelperSuffixes)
        {
            string name = HtmlUiView.MacHelperName + suffix;
            string helper = Path.Combine(frameworks, name + ".app", "Contents");
            Directory.CreateDirectory(Path.Combine(helper, "MacOS"));
            if (CopyIfDifferent(Path.Combine(source, "UnityCefHelper"), Path.Combine(helper, "MacOS", name))) copied++;
            string kind = suffix.Trim(' ', '(', ')').ToLowerInvariant();
            File.WriteAllText(Path.Combine(helper, "Info.plist"), HelperPlist(name, identifier + ".helper" + (kind.Length > 0 ? "." + kind : "")));
        }

        Debug.Log("CefPlayerPayload: assembled the Mac CEF payload in " + contents + " with " + copied + " changed file(s).");
    }

    private static string HelperPlist(string name, string identifier) => $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>{name}</string>
  <key>CFBundleIdentifier</key><string>{identifier}</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>{name}</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>{PlayerSettings.bundleVersion}</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSUIElement</key><string>1</string>
  <key>NSSupportsAutomaticGraphicsSwitching</key><true/>
</dict>
</plist>
";

    /// <summary>Rewrites a console PE's subsystem to GUI in place; false when already GUI, and fails on anything else.</summary>
    private static bool MakeWindowed(string exePath)
    {
        byte[] image = File.ReadAllBytes(exePath);
        if (image.Length < NewHeaderOffsetField + 4 || image[0] != (byte)'M' || image[1] != (byte)'Z')
        {
            throw new BuildFailedException(exePath + " is not a PE image (no MZ header).");
        }

        int peHeader = BitConverter.ToInt32(image, NewHeaderOffsetField);
        int optionalHeader = peHeader + CoffHeaderSize;
        int subsystemOffset = optionalHeader + SubsystemOffsetInOptionalHeader;
        if (peHeader < 0 || subsystemOffset + 2 > image.Length
            || image[peHeader] != (byte)'P' || image[peHeader + 1] != (byte)'E' || image[peHeader + 2] != 0 || image[peHeader + 3] != 0)
        {
            throw new BuildFailedException(exePath + " is not a PE image (no PE signature).");
        }

        ushort magic = BitConverter.ToUInt16(image, optionalHeader);
        if (magic != 0x10B && magic != 0x20B)
        {
            throw new BuildFailedException(exePath + " has an unknown optional header magic 0x" + magic.ToString("X") + ".");
        }

        ushort subsystem = BitConverter.ToUInt16(image, subsystemOffset);
        if (subsystem == WindowsGui) return false;
        if (subsystem != WindowsCui) throw new BuildFailedException(exePath + " has PE subsystem " + subsystem + ", neither console nor GUI.");

        using (FileStream stream = new FileStream(exePath, FileMode.Open, FileAccess.Write))
        {
            stream.Seek(subsystemOffset, SeekOrigin.Begin);
            stream.Write(BitConverter.GetBytes(WindowsGui), 0, 2);
        }
        return true;
    }

    // .meta files are Unity's, not CEF's, and a player has no use for them.
    private static int CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        int copied = 0;
        foreach (string file in Directory.GetFiles(source))
        {
            if (Path.GetExtension(file) == ".meta") continue;
            if (CopyIfDifferent(file, Path.Combine(destination, Path.GetFileName(file)))) copied++;
        }
        foreach (string directory in Directory.GetDirectories(source))
        {
            copied += CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
        return copied;
    }

    /// <summary>Copies changed content, including same-size native rebuilds.</summary>
    private static bool CopyIfDifferent(string file, string target)
    {
        if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(file).Length)
        {
            using var hash = System.Security.Cryptography.SHA256.Create();
            using var sourceStream = File.OpenRead(file);
            using var targetStream = File.OpenRead(target);
            if (Convert.ToBase64String(hash.ComputeHash(sourceStream)) ==
                Convert.ToBase64String(hash.ComputeHash(targetStream))) return false;
        }

        File.Copy(file, target, true);
        return true;
    }
}
#endif
