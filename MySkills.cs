using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TerminalNotes.MySkillsTests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TerminalNotes.LiveVerifyTests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TerminalNotes.AppLiveTests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TerminalNotes.AppLiveClaudeTests")]

namespace TerminalNotes;

/// <summary>나만의 스킬 (2026-11-11 사용자: "헤르메스처럼 자동축척해서 스킬로 계속 추가되게" → "계속학습되는 스킬은 나만의 스킬로 따로
/// 분류가 되어야해 그것도 수정,추가,삭제도 되어야하고 … 나중에 데이터가 엄청 용량이 커질텐데"): skills learned from finished work or
/// written by hand, kept apart from the store's. The library is the truth (%LOCALAPPDATA%\TerminalNotes\my-skills\&lt;name&gt;: skill.json,
/// body.md, the last versions); a skill that is on is copied to OpenCode's skill folder (with a marker file, so only ours are ever
/// removed from there). Names start with "my-" so they never meet a store skill's.
///
/// It stays small however long it learns: one skill's text is at most <see cref="MaxBodyChars"/>; at most <see cref="MaxApplied"/> are
/// on at once (every skill that is on puts its description into each request, so this is what really costs); at most
/// <see cref="MaxKept"/> are kept; learned skills nobody used for <see cref="UnusedOff"/> are turned off, skills that are off and
/// unused for <see cref="UnusedArchive"/> go to the archive (one zip), and the archive drops its oldest beyond
/// <see cref="MaxArchiveBytes"/>. Only <see cref="KeptVersions"/> earlier versions of a skill stay.</summary>
internal static class MySkills
{
    internal const string Category = "나만의 스킬";
    internal const string Prefix = "my-";
    internal const int MaxTitle = 80, MaxDescription = 300, MaxBodyChars = 8_000;
    internal const int MaxApplied = 30, MaxKept = 150, KeptVersions = 3;
    internal const long MaxArchiveBytes = 5L * 1024 * 1024;
    internal static readonly TimeSpan UnusedOff = TimeSpan.FromDays(30), UnusedArchive = TimeSpan.FromDays(90);
    private const string Marker = ".terminal-notes-my-skill";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly object Gate = new();

    internal sealed record Skill
    {
        public required string Name { get; init; }
        public required string Title { get; init; }
        public string Description { get; init; } = "";
        /// <summary>Learned from finished work (자동), not written by hand.</summary>
        public bool Learned { get; init; }
        /// <summary>The task folder it was learned in.</summary>
        public string? Project { get; init; }
        public DateTimeOffset Created { get; init; }
        public DateTimeOffset Updated { get; init; }
        public int Version { get; init; } = 1;
        public int Uses { get; init; }
        public DateTimeOffset? LastUsed { get; init; }
        public bool Applied { get; init; } = true;
        /// <summary>When it was last turned on by hand: the curator counts unused time from then.</summary>
        public DateTimeOffset? TurnedOn { get; init; }
        [JsonIgnore] public string Body { get; init; } = "";
        [JsonIgnore] public DateTimeOffset LastTouched => new[] { Updated, LastUsed ?? Updated, TurnedOn ?? Updated }.Max();
    }

    internal sealed record Usage(int Count, int Applied, long Bytes, int Archived, long ArchiveBytes);

    internal sealed record Settings
    {
        public bool AutoLearn { get; init; } = true;
        public DateTimeOffset? LastReview { get; init; }
        public int LearnedSinceReview { get; init; }
        /// <summary>Per OpenCode session, the last message already learned from.</summary>
        public Dictionary<string, string> LearnedUpTo { get; init; } = [];
        /// <summary>Merge proposals of the curator waiting for the user.</summary>
        public List<MergeProposal> Proposals { get; init; } = [];
        /// <summary>Learning rests until then: the model's usage limit was reached.</summary>
        public DateTimeOffset? PausedUntil { get; init; }
        /// <summary>Model requests learning made today (local date), against <see cref="MySkillLearning.MaxRequestsPerDay"/>.</summary>
        public string? RequestDay { get; init; }
        public int RequestCount { get; init; }
    }

    internal sealed record MergeProposal(string[] Names, string Title, string Description, string Body, string Reason);

    internal static string Root => AppContext.GetData("TerminalNotes.MySkillsDirectory") as string ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalNotes", "my-skills");
    private static string ArchivePath => Path.Combine(Root, "archive.zip");
    private static string SettingsPath => Path.Combine(Root, "settings.json");
    private static string Folder(string name) => Path.Combine(Root, name);
    // OpenCode's skill folder and Claude Code's: a skill that is on is in both (the same SKILL.md works for both).
    private static IEnumerable<string> SkillRoots => [InstalledSkills.GlobalDirectory, ClaudeCodeSessions.SkillsDirectory];
    private static string Deployed(string name) => Path.Combine(InstalledSkills.GlobalDirectory, name);

    internal static bool IsMine(string name) => name.StartsWith(Prefix, StringComparison.Ordinal) && InstalledSkills.ValidName(name);

    // ── reading ──

    internal static List<Skill> List()
    {
        lock (Gate)
        {
            if (!Directory.Exists(Root)) return [];
            return Directory.EnumerateDirectories(Root).Select(Path.GetFileName).OfType<string>().Where(IsMine)
                .Select(Read).OfType<Skill>().OrderByDescending(skill => skill.Updated).ToList();
        }
    }

    internal static Skill? Get(string name) { lock (Gate) return IsMine(name) ? Read(name) : null; }

    private static Skill? Read(string name)
    {
        string meta = Path.Combine(Folder(name), "skill.json"), body = Path.Combine(Folder(name), "body.md");
        try
        {
            if (!File.Exists(meta) || !File.Exists(body)) return null;
            var skill = JsonSerializer.Deserialize<Skill>(File.ReadAllText(meta, Utf8), Json);
            return skill is null || skill.Name != name ? null : skill with { Body = File.ReadAllText(body, Utf8) };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static Usage Measure()
    {
        lock (Gate)
        {
            var skills = List();
            long bytes = Directory.Exists(Root)
                ? Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Where(path => !path.Equals(ArchivePath, StringComparison.OrdinalIgnoreCase))
                    .Sum(path => new FileInfo(path).Length)
                : 0;
            var archived = Archived();
            return new Usage(skills.Count, skills.Count(skill => skill.Applied), bytes, archived.Count,
                File.Exists(ArchivePath) ? new FileInfo(ArchivePath).Length : 0);
        }
    }

    // ── writing ──

    /// <summary>A free "my-" name from a proposed one (kebab-case letters and digits) or, without one, from the title.</summary>
    internal static string NameFor(string? proposed, string title, bool forNew = true)
    {
        string slug = Regex.Replace((proposed ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.StartsWith("my-", StringComparison.Ordinal)) slug = slug[3..];
        if (slug.Length == 0)
            slug = "skill-" + Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(title + DateTimeOffset.UtcNow.Ticks)))[..8].ToLowerInvariant();
        string name = Prefix + (slug.Length > 50 ? slug[..50].Trim('-') : slug);
        if (!forNew) return name;
        string free = name;
        for (int i = 2; Directory.Exists(Folder(free)); i++) free = $"{name}-{i}";
        return free;
    }

    /// <summary>Adds a skill (name null or new) or saves a new version of one; the earlier text is kept among the last versions.</summary>
    internal static Skill Save(string? name, string title, string description, string body, bool learned = false, string? project = null,
        DateTimeOffset? now = null)
    {
        title = OneLine(title);
        description = OneLine(description);
        body = (body ?? "").Replace("\r\n", "\n").Trim();
        if (title.Length == 0) throw new ArgumentException("제목을 쓰세요.");
        if (title.Length > MaxTitle) throw new ArgumentException($"제목은 {MaxTitle}자까지입니다.");
        if (description.Length > MaxDescription) throw new ArgumentException($"설명은 {MaxDescription}자까지입니다.");
        if (body.Length == 0) throw new ArgumentException("지침을 쓰세요.");
        if (body.Length > MaxBodyChars) throw new ArgumentException($"지침은 {MaxBodyChars:N0}자까지입니다 · 지금 {body.Length:N0}자");
        var at = now ?? DateTimeOffset.UtcNow;
        lock (Gate)
        {
            var existing = name is not null && IsMine(name) ? Read(name) : null;
            if (existing is null && List().Count >= MaxKept)
            {
                // Room for a new one: the least used skill that is off goes to the archive.
                var spare = List().Where(skill => !skill.Applied).OrderBy(skill => skill.Uses).ThenBy(skill => skill.LastTouched).FirstOrDefault()
                    ?? throw new InvalidOperationException($"나만의 스킬은 {MaxKept}개까지입니다 · 안 쓰는 스킬을 보관하거나 지우세요.");
                ArchiveLocked(spare.Name);
            }
            string target = existing?.Name ?? NameFor(name, title);
            Directory.CreateDirectory(Folder(target));
            if (existing is not null && existing.Body != body)
            {
                string versions = Path.Combine(Folder(target), "versions");
                Directory.CreateDirectory(versions);
                File.WriteAllText(Path.Combine(versions, $"v{existing.Version}.md"), existing.Body, Utf8);
                foreach (var old in Directory.EnumerateFiles(versions, "v*.md").OrderByDescending(VersionOf).Skip(KeptVersions)) File.Delete(old);
            }
            bool turnOn = existing?.Applied ?? List().Count(skill => skill.Applied) < MaxApplied;
            var skill = new Skill
            {
                Name = target, Title = title, Description = description, Body = body,
                Learned = existing?.Learned ?? learned, Project = existing?.Project ?? project,
                Created = existing?.Created ?? at, Updated = existing is not null && existing.Body == body && existing.Title == title && existing.Description == description ? existing.Updated : at,
                Version = existing is null ? 1 : existing.Body == body ? existing.Version : existing.Version + 1,
                Uses = existing?.Uses ?? 0, LastUsed = existing?.LastUsed, Applied = turnOn
            };
            Write(skill);
            if (skill.Applied) Deploy(skill); else Undeploy(target);
            return skill;
        }
    }

    internal static IReadOnlyList<(int Version, string Body)> Versions(string name)
    {
        string versions = Path.Combine(Folder(name), "versions");
        if (!IsMine(name) || !Directory.Exists(versions)) return [];
        return Directory.EnumerateFiles(versions, "v*.md").OrderByDescending(VersionOf).Select(path => (VersionOf(path), File.ReadAllText(path, Utf8))).ToList();
    }

    private static int VersionOf(string path) => int.TryParse(Path.GetFileNameWithoutExtension(path).TrimStart('v'), out int version) ? version : 0;

    internal static void SetApplied(string name, bool applied)
    {
        lock (Gate)
        {
            var skill = Read(name) ?? throw new InvalidOperationException("나만의 스킬을 찾지 못했습니다.");
            if (applied && !skill.Applied && List().Count(other => other.Applied) >= MaxApplied)
                throw new InvalidOperationException($"한 번에 켤 수 있는 나만의 스킬은 {MaxApplied}개입니다 · 안 쓰는 스킬을 끄세요.");
            skill = skill with { Applied = applied, TurnedOn = applied && !skill.Applied ? DateTimeOffset.UtcNow : skill.TurnedOn };
            Write(skill);
            if (applied) Deploy(skill); else Undeploy(name);
        }
    }

    internal static void Delete(string name)
    {
        lock (Gate)
        {
            if (!IsMine(name)) return;
            Undeploy(name);
            if (Directory.Exists(Folder(name))) Directory.Delete(Folder(name), recursive: true);
            UpdateSettings(settings => settings with { Proposals = settings.Proposals.Where(proposal => !proposal.Names.Contains(name)).ToList() });
        }
    }

    /// <summary>Counts uses OpenCode made of my skills (its skill tool calls).</summary>
    internal static void RecordUses(IEnumerable<string> names, DateTimeOffset? at = null)
    {
        lock (Gate)
            foreach (var group in names.Where(IsMine).GroupBy(name => name))
                if (Read(group.Key) is { } skill)
                    Write(skill with { Uses = skill.Uses + group.Count(), LastUsed = at ?? DateTimeOffset.UtcNow });
    }

    // ── archive ──

    internal static void Archive(string name) { lock (Gate) ArchiveLocked(name); }

    private static void ArchiveLocked(string name)
    {
        var skill = Read(name) ?? throw new InvalidOperationException("나만의 스킬을 찾지 못했습니다.");
        Directory.CreateDirectory(Root);
        using (var zip = ZipFile.Open(ArchivePath, ZipArchiveMode.Update))
        {
            foreach (var entry in zip.Entries.Where(entry => entry.FullName.StartsWith(name + "/", StringComparison.Ordinal)).ToList()) entry.Delete();
            WriteEntry(zip, name + "/skill.json", JsonSerializer.Serialize(skill with { Applied = false }, Json));
            WriteEntry(zip, name + "/body.md", skill.Body);
        }
        Undeploy(name);
        Directory.Delete(Folder(name), recursive: true);
        TrimArchive();
    }

    private static void WriteEntry(ZipArchive zip, string path, string text)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.SmallestSize).Open();
        stream.Write(Utf8.GetBytes(text));
    }

    internal static List<Skill> Archived()
    {
        lock (Gate)
        {
            if (!File.Exists(ArchivePath)) return [];
            try
            {
                using var zip = ZipFile.OpenRead(ArchivePath);
                return zip.Entries.Where(entry => entry.FullName.EndsWith("/skill.json", StringComparison.Ordinal))
                    .Select(entry =>
                    {
                        using var reader = new StreamReader(entry.Open(), Utf8);
                        try { return JsonSerializer.Deserialize<Skill>(reader.ReadToEnd(), Json); }
                        catch (JsonException) { return null; }
                    })
                    .OfType<Skill>().OrderByDescending(skill => skill.LastTouched).ToList();
            }
            catch (InvalidDataException) { return []; }
        }
    }

    /// <summary>Back from the archive, off (turn it on when wanted).</summary>
    internal static void Restore(string name)
    {
        lock (Gate)
        {
            if (!IsMine(name) || !File.Exists(ArchivePath)) return;
            if (Directory.Exists(Folder(name))) throw new InvalidOperationException("같은 이름의 나만의 스킬이 이미 있습니다.");
            if (List().Count >= MaxKept) throw new InvalidOperationException($"나만의 스킬은 {MaxKept}개까지입니다 · 안 쓰는 스킬을 보관하거나 지우세요.");
            Skill? skill = null;
            string body = "";
            using (var zip = ZipFile.Open(ArchivePath, ZipArchiveMode.Update))
            {
                foreach (var entry in zip.Entries.Where(entry => entry.FullName.StartsWith(name + "/", StringComparison.Ordinal)).ToList())
                {
                    using (var reader = new StreamReader(entry.Open(), Utf8))
                    {
                        string text = reader.ReadToEnd();
                        if (entry.Name == "skill.json") skill = JsonSerializer.Deserialize<Skill>(text, Json);
                        else if (entry.Name == "body.md") body = text;
                    }
                    entry.Delete();
                }
            }
            if (skill is null) return;
            Directory.CreateDirectory(Folder(name));
            Write(skill with { Body = body, Applied = false, Updated = DateTimeOffset.UtcNow });
        }
    }

    internal static void DeleteArchived(string name)
    {
        lock (Gate)
        {
            if (!File.Exists(ArchivePath)) return;
            using var zip = ZipFile.Open(ArchivePath, ZipArchiveMode.Update);
            foreach (var entry in zip.Entries.Where(entry => entry.FullName.StartsWith(name + "/", StringComparison.Ordinal)).ToList()) entry.Delete();
        }
    }

    // The archive keeps its newest skills within its size.
    private static void TrimArchive()
    {
        if (!File.Exists(ArchivePath) || new FileInfo(ArchivePath).Length <= MaxArchiveBytes) return;
        var oldest = Archived().OrderBy(skill => skill.LastTouched).ToList();
        foreach (var skill in oldest)
        {
            DeleteArchived(skill.Name);
            if (new FileInfo(ArchivePath).Length <= MaxArchiveBytes * 9 / 10) break;
        }
    }

    // ── the curator ──

    internal sealed record Curation(IReadOnlyList<string> TurnedOff, IReadOnlyList<string> Archived);

    /// <summary>The rules that keep the library small (run at start and after each learned skill). Learned skills unused for
    /// <see cref="UnusedOff"/> are turned off; skills off and unused for <see cref="UnusedArchive"/> go to the archive; beyond the limits
    /// the least used go first. Folders in OpenCode's skill folder follow what is on.</summary>
    internal static Curation Curate(DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var off = new List<string>();
        var archived = new List<string>();
        lock (Gate)
        {
            var skills = List();
            foreach (var skill in skills.Where(skill => skill.Applied && skill.Learned && at - skill.LastTouched > UnusedOff))
            {
                Write(skill with { Applied = false });
                off.Add(skill.Name);
            }
            skills = List();
            foreach (var skill in skills.Where(skill => skill.Applied).OrderByDescending(skill => skill.LastTouched).Skip(MaxApplied))
            {
                Write(skill with { Applied = false });
                off.Add(skill.Name);
            }
            skills = List();
            foreach (var skill in skills.Where(skill => !skill.Applied && at - skill.LastTouched > UnusedArchive))
            {
                ArchiveLocked(skill.Name);
                archived.Add(skill.Name);
            }
            skills = List();
            foreach (var skill in skills.Where(skill => !skill.Applied).OrderBy(skill => skill.Uses).ThenBy(skill => skill.LastTouched).Take(Math.Max(0, skills.Count - MaxKept)))
            {
                ArchiveLocked(skill.Name);
                archived.Add(skill.Name);
            }
            // OpenCode's folder follows the library: ours that are off or gone are taken out, ours that are on are there.
            skills = List();
            foreach (string skillRoot in SkillRoots.Where(Directory.Exists))
                foreach (string folder in Directory.EnumerateDirectories(skillRoot))
                {
                    string name = Path.GetFileName(folder);
                    if (IsMine(name) && File.Exists(Path.Combine(folder, Marker)) && skills.All(skill => skill.Name != name || !skill.Applied)) Undeploy(name);
                }
            foreach (var skill in skills.Where(skill => skill.Applied && SkillRoots.Any(skillRoot => !File.Exists(Path.Combine(skillRoot, skill.Name, Marker))))) Deploy(skill);
            TrimArchive();
        }
        return new Curation(off, archived);
    }

    /// <summary>The curator also asks the model now and then which skills say the same and should become one (after 5 learned
    /// skills or a week).</summary>
    internal static bool ReviewDue(DateTimeOffset? now = null)
    {
        var settings = ReadSettings();
        return List().Count(skill => skill.Learned) >= 2 &&
            (settings.LearnedSinceReview >= 5 || settings.LastReview is null && settings.LearnedSinceReview >= 2 ||
             settings.LastReview is { } last && (now ?? DateTimeOffset.UtcNow) - last > TimeSpan.FromDays(7) && settings.LearnedSinceReview > 0);
    }

    /// <summary>Merges a proposal: the first skill becomes the merged text (a new version), the others go to the archive.</summary>
    internal static Skill AcceptProposal(MergeProposal proposal)
    {
        lock (Gate)
        {
            var present = proposal.Names.Where(name => Read(name) is not null).ToList();
            if (present.Count == 0) throw new InvalidOperationException("합칠 스킬이 이미 없습니다.");
            var merged = Save(present[0], proposal.Title, proposal.Description, proposal.Body);
            foreach (string other in present.Skip(1)) ArchiveLocked(other);
            DismissProposal(proposal);
            return merged;
        }
    }

    internal static void DismissProposal(MergeProposal proposal) =>
        UpdateSettings(settings => settings with { Proposals = settings.Proposals.Where(other => !other.Names.SequenceEqual(proposal.Names)).ToList() });

    // ── settings ──

    internal static Settings ReadSettings()
    {
        lock (Gate)
        {
            try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath, Utf8), Json) ?? new() : new(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
        }
    }

    internal static Settings UpdateSettings(Func<Settings, Settings> change)
    {
        lock (Gate)
        {
            var settings = change(ReadSettings());
            // Only the latest sessions are remembered.
            if (settings.LearnedUpTo.Count > 200)
                settings = settings with { LearnedUpTo = settings.LearnedUpTo.TakeLast(200).ToDictionary(pair => pair.Key, pair => pair.Value) };
            Directory.CreateDirectory(Root);
            WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, Json));
            return settings;
        }
    }

    // ── files ──

    private static void Write(Skill skill)
    {
        Directory.CreateDirectory(Folder(skill.Name));
        WriteAtomic(Path.Combine(Folder(skill.Name), "body.md"), skill.Body);
        WriteAtomic(Path.Combine(Folder(skill.Name), "skill.json"), JsonSerializer.Serialize(skill, Json));
        if (skill.Applied && File.Exists(Path.Combine(Deployed(skill.Name), Marker))) Deploy(skill);
    }

    private static void WriteAtomic(string path, string text)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text, Utf8);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The SKILL.md OpenCode reads.</summary>
    internal static string SkillText(Skill skill) =>
        $"---\nname: {skill.Name}\ndescription: {Quote(skill.Description.Length > 0 ? skill.Description : skill.Title)}\n---\n\n# {skill.Title}\n\n{skill.Body}\n";

    private static void Deploy(Skill skill)
    {
        foreach (string skillRoot in SkillRoots)
        {
            string folder = Path.Combine(skillRoot, skill.Name);
            // A folder of the same name that is not ours is left alone.
            if (Directory.Exists(folder) && !File.Exists(Path.Combine(folder, Marker)) && File.Exists(Path.Combine(folder, "SKILL.md"))) continue;
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, Marker), "나만의 스킬 · Terminal Notes가 관리합니다.\n", Utf8);
            WriteAtomic(Path.Combine(folder, "SKILL.md"), SkillText(skill));
        }
    }

    private static void Undeploy(string name)
    {
        if (!IsMine(name)) return;
        foreach (string skillRoot in SkillRoots)
        {
            string folder = Path.Combine(skillRoot, name);
            if (File.Exists(Path.Combine(folder, Marker))) Directory.Delete(folder, recursive: true);
        }
    }

    private static string OneLine(string? text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
