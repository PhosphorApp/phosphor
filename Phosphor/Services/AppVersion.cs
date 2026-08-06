using System.Reflection;

namespace Phosphor;

/// <summary>
/// Single source of truth for the human-readable application version string shown in logs and UI.
/// Reads the <see cref="AssemblyInformationalVersionAttribute"/> produced by Nerdbank.GitVersioning,
/// which carries the SemVer/tag (e.g. <c>1.0.37-rc.2</c>) rather than the numeric-only assembly version.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// The display version without a leading "v" (e.g. <c>1.0.37-rc.2</c>). The Git build metadata
    /// (the <c>+&lt;hash&gt;</c> suffix, when present) is trimmed for readability.
    /// </summary>
    public static string Display { get; } = Compute();

    /// <summary>The display version prefixed with "v" (e.g. <c>v1.0.37-rc.2</c>).</summary>
    public static string DisplayWithPrefix => $"v{Display}";

    private static string Compute()
    {
        var asm = Assembly.GetExecutingAssembly();
        var informational = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            // Nbgv format: "1.0.37-rc.2+abc1234" -> strip the "+<hash>" build metadata for display.
            var plus = informational.IndexOf('+');
            if (plus >= 0)
                informational = informational[..plus];
            return informational;
        }

        var asmVersion = asm.GetName().Version;
        return asmVersion is not null
            ? $"{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}.{asmVersion.Revision}"
            : "0.0";
    }
}
