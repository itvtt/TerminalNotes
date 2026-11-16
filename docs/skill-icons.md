# 스킬 아이콘 만들기

스킬 창(제목 표시줄의 **스킬**, 태스크의 **스킬…**)은 스킬을 앱스토어 목록처럼 한 줄에 하나씩 보여 줍니다. 줄 왼쪽의 아이콘은 **스킬의 카테고리**를 나타냅니다(제목의 앞글자가 아님). 지금은 Windows 아이콘 글꼴(Segoe Fluent Icons)의 글리프를 임시로 쓰고 있습니다. 이 문서대로 전용 아이콘을 벡터로 그려 바꿉니다.

## 지금 구조 (바꿀 곳)

| 위치 | 하는 일 |
|---|---|
| `SkillStore.cs` `RowIcon(glyph, tip)` | 40×40 타일(세로 그라데이션 `#2C2C2C`→`#1E1E1E`, 테두리 리소스 `ButtonLine`, 모서리 10) 가운데에 18px 금색 글리프. 툴팁과 접근성 이름은 `카테고리 · <카테고리 이름>` |
| `SkillStore.cs` `CategoryGlyphs` | 카테고리 이름에 든 낱말 → 글리프 표. **위에서부터 처음 맞는 줄**을 쓴다 |
| `SkillStore.cs` `SpareGlyphs`, `CategoryGlyph()` | 표에 없는 카테고리는 이름(앞뒤 공백 제거, 소문자)의 고정 해시(문자마다 `합 × 31 + 문자`)로 예비 아이콘 하나를 고른다. 같은 이름은 늘 같은 아이콘. `string.GetHashCode`는 실행마다 바뀌므로 쓰지 않는다 |
| `SkillStore.cs` `Card()` | `skill.CategoryName`(카테고리 없이 공유한 스킬은 `기타`)으로 아이콘을 고른다 |
| `SkillStore.Mine.cs` `MyCard()` | 나만의 스킬은 사람 아이콘(``) |

## 만들 아이콘

| 키 | 카테고리 이름에 든 낱말 (이 순서로 확인) | 뜻 | 지금 글리프 | 그릴 모양(제안) |
|---|---|---|---|---|
| `data` | 데이터, data, sql, 쿼리, db, 디비 | 데이터·조회 | `EE94` | 원통(위 타원 + 몸통 가로선 2개) |
| `report` | 보고, 리포트, report, 차트, 지표, 분석 | 보고서·지표 | `E9F9` | 문서 + 높이가 다른 막대 3개 |
| `code` | 코드, code, 개발, 리뷰, review, 프로그래밍 | 코드·리뷰 | `E943` | `{ }` 또는 `</>` |
| `release` | 배포, 릴리스, 릴리즈, release, deploy, 빌드, build | 배포·빌드 | `E898` | 위쪽 화살표 + 아래 받침선 |
| `doc` | 문서, doc, 가이드, guide, 안내, readme, 글쓰기 | 문서·안내 | `E8A5` | 모서리가 접힌 종이 + 글줄 2개 |
| `test` | 테스트, test, 검증, qa, 품질 | 테스트·검증 | `E9D5` | 체크 표시가 붙은 3줄 목록 |
| `security` | 보안, security, 권한, 인증 | 보안·권한 | `EA18` | 방패 |
| `ops` | 운영, 서버, 인프라, ops, 모니터, 장애 | 운영·모니터링 | `E9D9` | 모니터 안의 맥박선 |
| `design` | 디자인, design, ui, 화면, ux | 화면·디자인 | `E790` | 팔레트(물감 구멍 3~4개) |
| `config` | 설정, config, 환경 | 설정 | `E713` | 톱니바퀴 |
| `automation` | 자동화, automation, 스크립트, script | 자동화 | `E9F5` | 크기가 다른 톱니 두 개 |
| `learn` | 학습, 교육, 공부, learn | 학습 | `E7BE` | 학사모 |
| `other` | 기타, other, etc | 기타(카테고리 없이 공유) | `E8EC` | 꼬리표(태그) |
| `mine` | (나만의 스킬) | 내가 만들거나 작업에서 배운 스킬 | `E77B` | 사람(머리 + 어깨) |
| `spare-1` … `spare-8` | 표에 없는 카테고리 | 예비 | `E8B7` `EA86` `E81E` `F0E3` `E7C1` `E8F1` `EB05` `E8FD` | 폴더, 퍼즐, 겹친 층, 클립보드, 깃발, 책장, 원그래프, 목록: 서로 다른 단순한 모양 8개 |

- 낱말 순서를 지킨다. `build`와 `guide`에는 `ui`가 들어 있어서 `release`와 `doc`을 `design`보다 먼저 확인해야 한다.
- 표에 줄을 더하면 이 문서의 표도 같이 고친다.

## 그리는 규칙

- **격자**: 24×24, 바깥 2씩 비운다(실제로 그리는 곳은 가운데 20×20).
- **선 아이콘**: 선 굵기 1.5(24 격자 기준), 선 끝과 꺾이는 곳은 둥글게(Round), 면은 채우지 않는다. 지름 2 이하의 작은 점만 채워도 된다.
- **색은 하나**: 앱의 금색 리소스 `Accent`(`#E5C07B`)로만 그린다. 코드에 색 값을 직접 쓰지 말고 `SetResourceReference`로 연결한다. 다른 색, 그라데이션, 그림자는 쓰지 않는다. 어두운 빨강·갈색·브론즈 계열은 어디에도 쓰지 않는다.
- **구별**: 20px에서 모양이 서로 확실히 달라야 한다(비슷한 윤곽 금지). 16px로 줄여도 알아볼 수 있어야 한다.
- **글자 없음**: 아이콘 안에 글자·숫자를 넣지 않는다. 카테고리 이름은 툴팁에 있다.
- **타일은 그대로**: 40×40 바탕, 테두리, 모서리 10, 줄 높이, 글자 크기는 바꾸지 않는다. 아이콘은 타일 가운데 20×20에 놓는다.

## 형식과 넣는 법

1. 새 파일 `SkillIcons.cs`(`namespace TerminalNotes`, `internal static class SkillIcons`)를 만든다.
2. 아이콘마다 24×24 좌표의 경로 문자열 하나를 둔다. WPF 경로 문법(SVG의 `d`와 같은 `M L H V C Q A Z`, 소문자는 상대 좌표)으로 쓰고, 여러 획은 한 문자열에 이어 쓴다.
3. `internal static string KeyFor(string category)`: 위 표와 같은 낱말·순서·해시 규칙으로 키(`data` … `other`, `spare-1` … `spare-8`)를 돌려준다.
4. `internal static FrameworkElement Create(string key)`: `Geometry.Parse`로 만든 도형(Freeze)을 `Path`에 넣는다(`StrokeThickness = 1.5`, 선 끝·이음 Round, `Width`·`Height` 24, `Stroke`는 `Accent` 리소스). 이것을 `Viewbox`(20×20)에 담아 돌려준다.
5. `SkillStore.cs`: `RowIcon`이 글리프 대신 키를 받아 `SkillIcons.Create(key)`를 타일 가운데에 넣고, 타일의 `Tag`에 키를 둔다. 툴팁과 접근성 이름(`카테고리 · <이름>`)은 그대로 둔다. `Card()`는 `SkillIcons.KeyFor(category)`, `MyCard()`는 `mine`을 쓴다.
6. 다 바꾸면 `CategoryGlyphs`, `SpareGlyphs`, `CategoryGlyph()`를 지운다.

## 확인

```
dotnet build TerminalNotes.csproj -c Release
dotnet run --project tests\SkillStore -c Release -- --preview-shot
dotnet run --project tests\SkillStore -c Release
dotnet run --project tests\MySkills -c Release
```

- `--preview-shot`은 임시 폴더와 자기 중계 서버에 샘플 스킬 116개(카테고리 10개)를 만들어 `%TEMP%\TerminalNotes.SkillStore\store-preview.png`, `store-preview-category.png`, `store-preview-wide.png`(최대화)를 남긴다. 진짜 스토어와 이 PC의 스킬은 건드리지 않는다. `--preview`는 같은 창을 띄워 직접 볼 수 있고, 닫으면 끝난다.
- 스크린샷에서 카테고리마다 아이콘이 서로 다르고 또렷한지, 금색 선이 어두운 타일 위에서 잘 보이는지 본다.
- `tests\SkillStore\Program.cs`의 아이콘 검사(접근성 이름이 `카테고리 · `로 시작하는 `Border`)는 지금 글리프 글자를 비교한다. 이것을 `Tag == SkillIcons.KeyFor(카테고리)`, `KeyFor("데이터") == "data"`, `KeyFor("월간 보고서") == "report"`, `KeyFor("기타") == "other"`, `KeyFor("회계") == KeyFor(" 회계 ")` 검사로 바꾼다.

## 하지 않을 것

- PNG·ICO 같은 이미지 파일로 만들지 않는다(크기마다 흐려진다). 벡터 경로만 쓴다.
- 외부 아이콘 묶음을 내려받거나 인터넷 주소를 코드에 넣지 않는다.
- 줄 배치, 버튼(적용 / 적용 중 / 해제), 카테고리 목록 같은 다른 화면 요소는 바꾸지 않는다.
