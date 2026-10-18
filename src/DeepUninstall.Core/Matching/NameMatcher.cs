using System.Text;
using System.Text.RegularExpressions;
using DeepUninstall.Core.Discovery;
using DeepUninstall.Core.Models;

namespace DeepUninstall.Core.Matching;

/// <summary>A name a program may be known by.</summary>
/// <param name="Text">The name.</param>
/// <param name="IsFamily">
/// True when the name was produced by stripping a version ("Python" from "Python 3.13.15").
/// Such names are shared by every version of the product, so they are weaker evidence.
/// </param>
public readonly record struct NameVariant(string Text, bool IsFamily);

/// <summary>
/// Decides whether a folder or registry key name refers to a program.
/// </summary>
/// <remarks>
/// Matching is deliberately strict: after normalisation, names must be equal. Prefix or fuzzy
/// matches are not used, because "Fire" matching "Firefox" is how uninstallers delete the wrong
/// things. Names that are too generic, or that another installed program also uses, are dropped,
/// and folders carrying a different version number ("Python 3.14" when removing 3.13) are rejected.
/// </remarks>
public sealed partial class NameMatcher
{
    private static readonly HashSet<string> GenericNames = new(StringComparer.Ordinal)
    {
        "microsoft", "windows", "common", "commonfiles", "system", "system32", "syswow64", "program", "programs",
        "programfiles", "programdata", "temp", "tmp", "data", "app", "apps", "application", "applications",
        "software", "update", "updates", "updater", "setup", "installer", "install", "installation", "uninstall",
        "uninstaller", "tools", "tool", "utilities", "utility", "bin", "lib", "cache", "caches", "config",
        "configuration", "settings", "user", "users", "default", "public", "packages", "package", "plugins", "plugin",
        "addons", "addin", "addins", "extensions", "drivers", "driver", "runtime", "runtimes", "service", "services",
        "helper", "launcher", "client", "server", "games", "game", "office", "classes", "policies", "wow6432node",
        "clients", "registeredapplications", "local", "locallow", "roaming", "documents", "downloads", "desktop",
        "startmenu", "startup", "google", "intel", "nvidia", "nvidiacorporation", "amd", "realtek", "adobe", "apple",
        "oracle", "mozilla", "x64", "x86", "net", "dotnet", "framework", "redistributable", "sdk", "documentation",
        "help", "readme", "license", "logs", "log", "backup", "backups", "crashdumps", "crashreports", "deepuninstall",
        "visualstudio", "shared", "components", "modules", "resources", "assets", "media", "fonts", "themes",
        "profiles", "profile", "inc", "llc", "ltd", "corporation", "company", "free", "pro",
        "home", "portable", "beta", "preview", "insider", "test", "demo", "trial", "vendor", "unknown",
    };

    private readonly HashSet<string> _exact;
    private readonly HashSet<string> _family;
    private readonly HashSet<string> _publishers;
    private readonly List<string> _versions;

    /// <param name="productNames">Names the program is known by.</param>
    /// <param name="publisherNames">Names its publisher is known by.</param>
    /// <param name="namesUsedByOtherPrograms">Names of other installed programs; these are never matched.</param>
    /// <param name="versions">The program's version numbers, used to reject folders of other versions.</param>
    public NameMatcher(
        IEnumerable<NameVariant> productNames,
        IEnumerable<string> publisherNames,
        IEnumerable<string>? namesUsedByOtherPrograms = null,
        IEnumerable<string>? versions = null)
    {
        var shared = new HashSet<string>(namesUsedByOtherPrograms?.Select(Normalize) ?? [], StringComparer.Ordinal);
        var usable = productNames
            .Select(v => v with { Text = Normalize(v.Text) })
            .Where(v => IsUsableName(v.Text) && !shared.Contains(v.Text))
            .ToList();

        _exact = usable.Where(v => !v.IsFamily).Select(v => v.Text).ToHashSet(StringComparer.Ordinal);
        _family = usable.Where(v => v.IsFamily && !_exact.Contains(v.Text)).Select(v => v.Text).ToHashSet(StringComparer.Ordinal);
        _publishers = publisherNames.Select(Normalize).Where(n => n.Length >= 3).ToHashSet(StringComparer.Ordinal);
        _versions = versions?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList() ?? [];
    }

    /// <summary>Convenience constructor for exact names only.</summary>
    public NameMatcher(IEnumerable<string> productNames, IEnumerable<string> publisherNames, IEnumerable<string>? namesUsedByOtherPrograms = null)
        : this(productNames.Select(n => new NameVariant(n, false)), publisherNames, namesUsedByOtherPrograms)
    {
    }

    public IReadOnlyCollection<string> ProductNames => [.. _exact, .. _family];
    public IReadOnlyCollection<string> ExactProductNames => _exact;
    public IReadOnlyCollection<string> PublisherNames => _publishers;

    public static NameMatcher ForApp(InstalledApp app, IEnumerable<InstalledApp> otherApps, bool includeInstallFolderName = true)
    {
        var shared = otherApps.SelectMany(o => ProductNameVariants(o, includeInstallFolderName)).Select(v => v.Text);
        var versions = VersionTokens(app.DisplayName).ToList();
        if (!string.IsNullOrWhiteSpace(app.DisplayVersion)) versions.Add(app.DisplayVersion);
        return new NameMatcher(ProductNameVariants(app, includeInstallFolderName), PublisherVariants(app.Publisher), shared, versions);
    }

    /// <returns>The confidence of the match, or null when the name does not refer to the program.</returns>
    public Confidence? MatchProduct(string name)
    {
        if (HasConflictingVersion(name)) return null;

        Confidence? best = null;
        foreach (var variant in Variants(name, null))
        {
            var n = Normalize(variant.Text);
            Confidence? c = _exact.Contains(n) ? (n.Length >= 5 ? Confidence.High : Confidence.Medium)
                : _family.Contains(n) ? Confidence.Medium
                : null;
            if (c > best || (c is not null && best is null)) best = c;
        }
        return best;
    }

    /// <summary>
    /// Matching inside a folder or key already known to be the publisher's (e.g. HKCU\Software\Martin Prikryl).
    /// The container is extra evidence, so short names and a trailing major version ("WinSCP 2") are accepted.
    /// </summary>
    public Confidence? MatchProductInPublisherContainer(string name)
    {
        if (HasConflictingVersion(name)) return null;

        foreach (var variant in Variants(name, null))
        {
            var n = Normalize(variant.Text);
            if (_exact.Contains(n)) return Confidence.High;
            var trimmed = n.TrimEnd("0123456789".ToCharArray());
            if (trimmed.Length < n.Length && _exact.Contains(trimmed)) return Confidence.High;
        }
        return MatchProduct(name) is null ? null : Confidence.Medium;
    }

    public bool MatchPublisher(string name)
    {
        if (_publishers.Count == 0) return false;
        return _publishers.Contains(Normalize(name)) || PublisherVariants(name).Any(v => _publishers.Contains(Normalize(v)));
    }

    /// <summary>
    /// Looser check used only together with hard evidence (e.g. a ProgID whose open command
    /// points into the install folder): does the name contain the product or publisher name?
    /// </summary>
    public bool NameContainsProductOrPublisher(string name)
    {
        var n = Normalize(name);
        return ProductNames.Any(p => p.Length >= 4 && n.Contains(p, StringComparison.Ordinal))
               || _publishers.Any(p => p.Length >= 4 && !GenericNames.Contains(p) && n.Contains(p, StringComparison.Ordinal));
    }

    public static IEnumerable<NameVariant> ProductNameVariants(InstalledApp app, bool includeInstallFolderName = true)
    {
        foreach (var v in Variants(app.DisplayName, app.Publisher)) yield return v;

        var keyName = app.KeyName;
        if (!InstalledAppScanner.IsGuid(keyName))
        {
            if (keyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase)) keyName = keyName[..^4];
            foreach (var v in Variants(keyName, app.Publisher)) yield return v;
        }

        if (includeInstallFolderName && !string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            var folder = Path.GetFileName(app.InstallLocation.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(folder) && !folder.EndsWith(':'))
                foreach (var v in Variants(folder, app.Publisher)) yield return v;
        }
    }

    [GeneratedRegex(@"\s*[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"[\s_-]+(v|version\s*)?\d+([._]\d+)+[\w.\-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingDottedVersion();

    [GeneratedRegex(@"[\s_-]+v\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVPrefixedVersion();

    [GeneratedRegex(@"\b(x64|x86|amd64|arm64|win64|win32|64-bit|32-bit|64 bit|32 bit)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureTokens();

    [GeneratedRegex(@"\d+(\.\d+)+")]
    private static partial Regex DottedVersion();

    [GeneratedRegex(@"[,\.]?\s+(inc|llc|ltd|limited|corp|corporation|co|gmbh|ag|s\.?a|b\.?v|srl|pty|plc|oy|ab|as|kg|s\.?r\.?o|sas|sarl|spa|kk)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex LegalSuffix();

    [GeneratedRegex(@"\s+(software|technologies|technology|systems|studios|studio|labs|group|international|solutions|development|developers|team|project|foundation|corporation)$", RegexOptions.IgnoreCase)]
    private static partial Regex PublisherTrailingWord();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleSpaces();

    /// <summary>Variants of a program name with brackets, versions and architecture tags removed.</summary>
    public static IReadOnlyList<string> NameVariants(string name, string? publisher) =>
        Variants(name, publisher).Select(v => v.Text).ToList();

    internal static IReadOnlyList<NameVariant> Variants(string name, string? publisher)
    {
        var results = new List<NameVariant>();
        void Add(string s, bool family)
        {
            s = MultipleSpaces().Replace(s, " ").Trim(' ', '-', '_', '.', ',', ':', '–');
            if (s.Length == 0) return;
            var existing = results.FindIndex(r => r.Text.Equals(s, StringComparison.OrdinalIgnoreCase));
            if (existing < 0) results.Add(new NameVariant(s, family));
            else if (!family && results[existing].IsFamily) results[existing] = new NameVariant(s, false);
        }

        Add(name, false);
        var noBrackets = Brackets().Replace(name, " ");
        Add(noBrackets, false);
        var noArch = ArchitectureTokens().Replace(noBrackets, " ").Trim();
        Add(noArch, false);
        var noVersion = TrailingVPrefixedVersion().Replace(TrailingDottedVersion().Replace(noArch, ""), "").Trim();
        var strippedVersion = !noVersion.Equals(MultipleSpaces().Replace(noArch, " ").Trim(), StringComparison.OrdinalIgnoreCase);
        Add(noVersion, strippedVersion);

        var dash = noVersion.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) Add(noVersion[..dash], strippedVersion);

        foreach (var pub in PublisherVariants(publisher))
        {
            foreach (var r in results.ToList())
            {
                if (r.Text.Length > pub.Length + 1 && r.Text.StartsWith(pub + " ", StringComparison.OrdinalIgnoreCase))
                    Add(r.Text[(pub.Length + 1)..], r.IsFamily);
            }
        }

        return results;
    }

    public static IReadOnlyList<string> PublisherVariants(string? publisher)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(publisher)) return results;

        void Add(string s)
        {
            s = s.Trim(' ', ',', '.', '-');
            if (s.Length > 0 && !results.Contains(s, StringComparer.OrdinalIgnoreCase)) results.Add(s);
        }

        var current = Brackets().Replace(publisher, " ").Trim();
        Add(current);
        for (var i = 0; i < 3; i++)
        {
            var next = LegalSuffix().Replace(current, "").Trim();
            if (next == current) break;
            current = next;
            Add(current);
        }
        var noTrailingWord = PublisherTrailingWord().Replace(current, "").Trim();
        if (noTrailingWord.Length >= 3) Add(noTrailingWord);

        return results;
    }

    /// <summary>Lower-case, letters and digits only (plus '+' and '#', so "Notepad++" stays distinct).</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '+' or '#') sb.Append(c);
        }
        return sb.ToString();
    }

    internal static IEnumerable<string> VersionTokens(string text) =>
        DottedVersion().Matches(text).Select(m => m.Value);

    /// <summary>True if the name carries a version number that is not the program's version.</summary>
    private bool HasConflictingVersion(string name)
    {
        if (_versions.Count == 0) return false;
        var tokens = VersionTokens(name).ToList();
        if (tokens.Count == 0) return false;
        return !tokens.Any(t => _versions.Any(v => IsVersionPrefix(t, v) || IsVersionPrefix(v, t)));
    }

    private static bool IsVersionPrefix(string prefix, string version) =>
        version.StartsWith(prefix, StringComparison.Ordinal)
        && (version.Length == prefix.Length || !char.IsDigit(version[prefix.Length]));

    private static bool IsUsableName(string normalized) =>
        normalized.Length >= 3
        && !GenericNames.Contains(normalized)
        && !normalized.All(char.IsDigit)
        && !(normalized.Length == 32 && normalized.All(char.IsAsciiHexDigit));
}
