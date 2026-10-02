using System;
using System.IO;
using AetherFrame.Services.Network.Personas;
using Xunit;

namespace AetherFrame.Personas.Tests;

/// <summary>
/// How the Sharing window shows a path (N2-5c's path rule, kept after V4 removed the Personas
/// window): a path under the application data folder or the user's profile never shows the Windows
/// user name.
/// </summary>
public sealed class PersonaPathsTests
{
    [Fact]
    public void APathUnderApplicationData_HidesTheUserName_AndAnyOtherPathIsShownAsItIs()
    {
        var root = Path.Combine("C:", "Users", "SomeoneSecret", "AppData", "Roaming");
        var keys = Path.Combine(root, "XIVLauncher", "pluginConfigs", "AetherFrame", "Network", "Personas", "keys");
        var shown = PersonaPaths.DisplayPath(keys, root);
        Assert.Equal("%APPDATA%" + keys[root.Length..], shown);
        Assert.DoesNotContain("SomeoneSecret", shown, StringComparison.Ordinal);

        // A sibling folder that merely starts with the same text is not under it.
        var sibling = root + "Other" + Path.DirectorySeparatorChar + "x";
        Assert.Equal(sibling, PersonaPaths.DisplayPath(sibling, root));
        Assert.Equal(keys, PersonaPaths.DisplayPath(keys, ""));
        Assert.Equal("%APPDATA%" + keys[root.Length..], PersonaPaths.DisplayPath(keys, root + Path.DirectorySeparatorChar));

        // A launcher installed elsewhere under the profile, in Documents say: from %USERPROFILE% on.
        var profile = Path.Combine("C:", "Users", "SomeoneSecret");
        var documents = Path.Combine(profile, "Documents", "Launcher", "pluginConfigs", "AetherFrame");
        var shownFromProfile = PersonaPaths.DisplayPath(documents, root, profile);
        Assert.Equal("%USERPROFILE%" + documents[profile.Length..], shownFromProfile);
        Assert.DoesNotContain("SomeoneSecret", shownFromProfile, StringComparison.Ordinal);
        Assert.Equal("%APPDATA%" + keys[root.Length..], PersonaPaths.DisplayPath(keys, root, profile));
    }
}
