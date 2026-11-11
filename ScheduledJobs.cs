using System.IO;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TerminalNotes.HerdrExtrasTests")]

namespace TerminalNotes;

/// <summary>예약 작업, like Hermes Agent's scheduled jobs (2026-11-11 사용자 선택): an instruction sent to a task's terminal on chosen
/// days at a time ("매일 9시 빌드 확인"). Unlike 예약 실행 (one start of a task's plan) it repeats. A job due while the agent is busy
/// waits up to <see cref="BusyWait"/>; one missed by more than <see cref="MissedWindow"/> (the app was closed) is skipped.</summary>
internal static class ScheduledJobs
{
    internal static readonly TimeSpan BusyWait = TimeSpan.FromMinutes(30), MissedWindow = TimeSpan.FromHours(6);
    internal const int EveryDay = 0b111_1111, Weekdays = 0b001_1111, Weekend = 0b110_0000;
    internal static readonly string[] DayNames = ["월", "화", "수", "목", "금", "토", "일"];

    internal sealed record Job
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public required string Project { get; init; }
        public required string Instruction { get; init; }
        /// <summary>Bit 0 월 … bit 6 일.</summary>
        public int Days { get; init; } = EveryDay;
        public string Time { get; init; } = "09:00";
        public bool Enabled { get; init; } = true;
        public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
        public DateTimeOffset? LastRun { get; init; }
        public string? LastResult { get; init; }
    }

    internal enum Due { NotYet, Now, Missed }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static string FilePath => AppContext.GetData("TerminalNotes.ScheduledJobsPath") as string ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalNotes", "scheduled-jobs.json");

    internal static List<Job> List()
    {
        lock (Gate)
        {
            try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(FilePath), Json) ?? [] : []; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return []; }
        }
    }

    internal static Job Save(Job job)
    {
        if (string.IsNullOrWhiteSpace(job.Instruction)) throw new ArgumentException("보낼 지시를 쓰세요.");
        if (job.Instruction.Length > 4000) throw new ArgumentException("지시는 4,000자까지입니다.");
        if (ParseTime(job.Time) is null) throw new ArgumentException("시각은 09:00처럼 쓰세요.");
        if ((job.Days & EveryDay) == 0) throw new ArgumentException("요일을 하나 이상 고르세요.");
        if (!Directory.Exists(job.Project)) throw new ArgumentException("태스크 폴더를 찾을 수 없습니다.");
        job = job with { Time = ParseTime(job.Time)!.Value.ToString(@"hh\:mm"), Instruction = job.Instruction.Trim() };
        lock (Gate)
        {
            var jobs = List();
            int index = jobs.FindIndex(other => other.Id == job.Id);
            if (index >= 0) jobs[index] = job; else jobs.Add(job);
            Write(jobs);
        }
        return job;
    }

    internal static void Delete(string id) { lock (Gate) Write(List().Where(job => job.Id != id).ToList()); }

    internal static void SetEnabled(string id, bool enabled) => Change(id, job => job with { Enabled = enabled });

    internal static void MarkRun(string id, DateTimeOffset at, string result) => Change(id, job => job with { LastRun = at, LastResult = result });

    private static void Change(string id, Func<Job, Job> change)
    {
        lock (Gate)
        {
            var jobs = List();
            int index = jobs.FindIndex(job => job.Id == id);
            if (index < 0) return;
            jobs[index] = change(jobs[index]);
            Write(jobs);
        }
    }

    internal static TimeSpan? ParseTime(string? text) =>
        TimeSpan.TryParseExact((text ?? "").Trim(), [@"h\:mm", @"hh\:mm"], null, out var time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1) ? time : null;

    /// <summary>The first run after <paramref name="after"/> (local time).</summary>
    internal static DateTime? NextRun(Job job, DateTime after)
    {
        if (ParseTime(job.Time) is not { } time) return null;
        for (int offset = 0; offset <= 7; offset++)
        {
            var day = after.Date.AddDays(offset);
            int bit = ((int)day.DayOfWeek + 6) % 7; // 월 = 0
            if ((job.Days & (1 << bit)) == 0) continue;
            var at = day + time;
            if (at > after) return at;
        }
        return null;
    }

    /// <summary>Whether a job runs now: its next time after the last run (or its creation) has come; long past means it was missed.</summary>
    internal static (Due Due, DateTime? At) Check(Job job, DateTime now)
    {
        if (!job.Enabled) return (Due.NotYet, null);
        var since = (job.LastRun ?? job.Created).LocalDateTime;
        if (NextRun(job, since) is not { } at || at > now) return (Due.NotYet, null);
        return (now - at > MissedWindow ? Due.Missed : Due.Now, at);
    }

    internal static string When(Job job)
    {
        string days = job.Days switch
        {
            EveryDay => "매일", Weekdays => "평일", Weekend => "주말",
            _ => string.Join("·", DayNames.Where((_, index) => (job.Days & (1 << index)) != 0))
        };
        return $"{days} {job.Time}";
    }

    private static void Write(List<Job> jobs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(jobs, Json));
        File.Move(FilePath + ".tmp", FilePath, overwrite: true);
    }
}
