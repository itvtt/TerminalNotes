using System.IO;
using System.Net.Http;
using System.Windows;
using TerminalNotes;

// The store window with many sample skills, to look at how a full store reads (2026-11-13 사용자: "스킬 샘플을 100개 이상으로 만들어").
// Everything is in a temporary folder with its own loopback relay, like the test: the real store and this PC's skills are untouched.
//   dotnet run --project tests\SkillStore -c Release -- --preview        the window, until it is closed
//   dotnet run --project tests\SkillStore -c Release -- --preview-shot   store-preview.png in %TEMP%\TerminalNotes.SkillStore, then quits
internal static partial class Program
{
    private static readonly (string Category, string[] Titles)[] Samples =
    [
        ("데이터", ["Impala로 데이터 조회", "Trino 쿼리 작성", "SQL 규칙", "데이터 품질 점검", "파티션 테이블 설계", "CSV 정리", "엑셀 피벗 만들기",
            "지표 정의서", "데이터 사전 작성", "중복 행 찾기", "시계열 집계", "쿼리 성능 개선", "샘플 데이터 만들기", "스키마 변경 기록"]),
        ("보고서", ["보고서 양식", "주간 보고 정리", "월간 지표 요약", "회의록 정리", "표 머리글 규칙", "차트 색 규칙", "요약 세 줄 쓰기", "결론 먼저 쓰기",
            "숫자 표기 규칙", "출처 표기", "보고서 맞춤법 점검", "발표 자료 개요"]),
        ("코드 리뷰", ["리뷰 체크리스트", "이름 짓기 규칙", "예외 처리 점검", "로그 남기기 규칙", "주석 정리", "중복 코드 찾기", "테스트 없는 변경 막기",
            "PR 설명 쓰기", "커밋 메시지 규칙", "의존성 추가 검토", "성능 회귀 점검", "보안 취약점 점검"]),
        ("배포", ["릴리스 노트 작성", "버전 올리기", "배포 전 점검", "롤백 절차", "설정 파일 점검", "환경 변수 정리", "빌드 캐시 정리", "설치 파일 만들기",
            "서명 확인", "변경 공지 쓰기", "핫픽스 절차", "배포 일정 정리"]),
        ("문서", ["README 정리", "API 문서 쓰기", "설치 안내 쓰기", "용어집 관리", "변경 이력 쓰기", "사용자 안내 쓰기", "FAQ 정리", "그림 설명 붙이기",
            "목차 만들기", "링크 점검", "문서 번역 규칙", "예제 코드 넣기"]),
        ("테스트", ["단위 테스트 쓰기", "UI 스크린샷 확인", "회귀 테스트 목록", "테스트 데이터 정리", "경계값 점검", "느린 테스트 찾기", "모의 객체 규칙",
            "통합 테스트 절차", "테스트 이름 규칙", "실패 원인 기록", "커버리지 보기", "부하 테스트"]),
        ("보안", ["비밀값 커밋 막기", "권한 점검", "입력 검증", "로그 개인정보 가리기", "토큰 만료 처리", "외부 링크 검사", "파일 업로드 검사", "암호 규칙",
            "감사 로그", "의존성 취약점"]),
        ("운영", ["장애 기록 쓰기", "디스크 용량 점검", "메모리 사용 점검", "서비스 재시작 절차", "로그 보관 규칙", "백업 확인", "알림 규칙", "작업 예약 점검",
            "서버 상태 요약", "인증서 만료 확인", "포트 점검", "프로세스 정리"]),
        ("디자인", ["화면 문구 다듬기", "색 대비 점검", "버튼 배치 규칙", "아이콘 규칙", "여백 규칙", "다크 테마 점검", "글꼴 규칙", "빈 화면 문구",
            "오류 문구 쓰기", "단축키 안내"]),
        ("", ["할 일 정리", "질문 먼저 하기", "한국어로 답하기", "짧게 답하기", "작업 시간 기록", "메모 정리", "일정 정리", "이메일 초안", "번역 다듬기", "요약하기"]),
    ];

    private static async Task PreviewAsync(bool interactive)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes skill preview " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        AppContext.SetData("TerminalNotes.SkillsDirectory", Path.Combine(fixture, "opencode skills"));
        AppContext.SetData("TerminalNotes.PluginsDirectory", Path.Combine(fixture, "opencode plugins"));
        AppContext.SetData("TerminalNotes.SkillFavoritesPath", Path.Combine(fixture, "skill-favorites.json"));
        AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "data"));
        AppContext.SetData("TerminalNotes.MySkillsDirectory", Path.Combine(fixture, "my-skills"));
        AppContext.SetData("TerminalNotes.ClaudeSkillsDirectory", Path.Combine(fixture, "claude-skills"));
        SkillStoreAdmins.AccountOverride = () => "DOMAIN\\not-a-store-admin";
        int port = FreePort();
        const string key = "skill-preview-key";
        // Some 150 shares in a few seconds: above the relay's usual 60 an hour from one address.
        var (server, output) = StartRelay(fixture, port, key, "skill-preview-admin", "RELAY_UPLOADS_PER_HOUR=10000");
        using var relayProcess = server;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string relay = $"http://127.0.0.1:{port}";
            await Until(async () =>
            {
                if (server.HasExited) throw new InvalidOperationException("The relay exited: " + output);
                try { return await http.GetStringAsync(relay + "/health") == "ok"; }
                catch (HttpRequestException) { return false; }
            }, "the relay to start");
            var store = new SkillStoreClient(new Uri(relay), key, http);
            int count = await SeedAsync(store);
            Console.WriteLine($"PREVIEW {count} sample skills");
            var dialog = new SkillStoreDialog(store);
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            if (interactive) dialog.Show();
            else Open(dialog);
            await Until(() => Cards(dialog).Count > 20, "the sample cards");
            if (interactive)
            {
                dialog.Activate();
                await closed.Task;
                return;
            }
            await SaveImage(dialog, "store-preview.png");
            dialog.Category = "데이터";
            await Task.Delay(300);
            await SaveImage(dialog, "store-preview-category.png");
            dialog.WindowState = WindowState.Maximized;
            dialog.Category = "";
            await Task.Delay(500);
            await SaveImage(dialog, "store-preview-wide.png");
            dialog.Close();
        }
        finally
        {
            try { server.Kill(entireProcessTree: true); server.WaitForExit(5000); }
            catch (InvalidOperationException) { }
            try { Directory.Delete(fixture, recursive: true); }
            catch (IOException) { }
        }
    }

    // About 120 skills in ten categories (the last without one, so 기타), from several authors with some of this PC's account (a few
    // private), some at v2 and v3, some with a 출처, some applied, two with a newer version than the applied one, and a few starred.
    private static async Task<int> SeedAsync(SkillStoreClient store)
    {
        string me = Environment.UserName;
        string[] authors = ["alice", "bob", "carol", "dave", "erin", me];
        var favorites = SkillFavorites.Load();
        int index = 0;
        foreach (var (category, titles) in Samples)
            foreach (string title in titles)
            {
                index++;
                string name = $"sample-{index:000}", author = authors[index % authors.Length];
                string Body(int version) => $"- {title} 절차를 따른다.\n- 결과는 세 줄로 요약한다.\n" + (version > 1 ? $"- v{version}: 예외 경우를 먼저 확인한다.\n" : "");
                await Retry(() => store.PublishAsync(name, title, $"{title}할 때 쓰는 지침", Body(1), author, default, category: category,
                    isPrivate: author == me && index % 4 == 0, origin: index % 7 == 0 ? $"https://example.com/guides/{name}" : null));
                bool updatable = index is 3 or 16;
                if (updatable || index % 6 == 0) InstalledSkills.Apply(await Retry(() => store.GetVersionAsync(name, null, apply: true, default)));
                if (updatable || index % 5 == 0) await Retry(() => store.PublishAsync(name, title, $"{title}할 때 쓰는 지침", Body(2), author, default, note: "예외 경우 추가"));
                if (index % 15 == 0) await Retry(() => store.PublishAsync(name, title, $"{title}할 때 쓰는 지침", Body(3), author, default, note: "정리"));
                if (index % 11 == 0) SkillFavorites.Set(favorites, name, true);
            }
        return index;
    }

    // The loopback relay now and then drops a connection under ~150 quick requests: try again a few times.
    private static async Task<T> Retry<T>(Func<Task<T>> action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await action(); }
            catch (SkillStoreException) when (attempt < 4) { await Task.Delay(300 * attempt); }
        }
    }
}
