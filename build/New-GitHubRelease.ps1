# 빌드한 파일들로 GitHub Release 를 만듭니다 (.github\workflows\release.yml 의 마지막 단계). AppKit 의 같은 이름 스크립트와 같음.
#   초안으로 만들어 파일을 모두 올린 뒤 공개: 파일이 빠진 릴리스가 "최신"으로 잡혀 설치된 앱이 헛걸음하지 않게.
#   올리다 실패하면 초안을 지우고 실패로 끝냄 (다음 실행이 같은 번호로 다시 시도).
#   릴리스 노트: 지난 릴리스 태그 이후의 커밋 제목 (병합 커밋 제외).
# 환경 변수: GH_TOKEN (contents: write), GITHUB_API_URL, GITHUB_REPOSITORY, GITHUB_SHA — Actions 가 넣어 줌.
#           RELEASE_VERSION, RELEASE_ASSETS(올릴 파일, ; 로 구분), RELEASE_TITLE(예: i-luncher), RELEASE_PREVIOUS(지난 태그) — 워크플로가 넣어 줌.
# github.com 과 사내 GitHub Enterprise 모두 같은 API 입니다.
param(
    [string]$Version = $env:RELEASE_VERSION,
    [string]$Assets = $env:RELEASE_ASSETS,
    [string]$Title = $env:RELEASE_TITLE,
    [string]$Previous = $env:RELEASE_PREVIOUS,
    [string]$Footer = $env:RELEASE_FOOTER
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
foreach ($name in 'GH_TOKEN', 'GITHUB_API_URL', 'GITHUB_REPOSITORY', 'GITHUB_SHA') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is not set." }
}
if (-not $Version -or -not $Assets) { throw 'Version and Assets are required (RELEASE_VERSION, RELEASE_ASSETS).' }
if (-not $Title) { $Title = ($env:GITHUB_REPOSITORY -split '/')[-1] }
$files = @($Assets -split ';' | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() })
foreach ($file in $files) { if (-not (Test-Path -LiteralPath $file)) { throw "Release file not found: $file" } }

$api = "$($env:GITHUB_API_URL.TrimEnd('/'))/repos/$env:GITHUB_REPOSITORY"
$headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json' }
function Send-Json([string]$Method, [string]$Uri, $Data) {
    Invoke-RestMethod -UseBasicParsing -Method $Method -Uri $Uri -Headers $headers -ContentType 'application/json; charset=utf-8' `
        -Body ([Text.Encoding]::UTF8.GetBytes(($Data | ConvertTo-Json)))
}

$log = @()
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ((Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $repositoryRoot '.git'))) {
    $previousEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    try {
        $range = @('-n', '30')
        if ($Previous) {
            & git -C $repositoryRoot rev-parse -q --verify "refs/tags/$Previous" | Out-Null
            if ($LASTEXITCODE -eq 0) { $range = @("$Previous..HEAD") }
        }
        $log = @(& git -C $repositoryRoot log --no-merges --pretty=format:'- %s' @range)
    }
    finally { [Console]::OutputEncoding = $previousEncoding }
}
$notes = if ($log.Count) { "## 변경 사항`n`n" + ($log -join "`n") } else { '변경 사항 없음' }
if ($Footer) { $notes += "`n`n$Footer" }

# 릴리스 파일 올리기. Windows PowerShell 5.1 의 Invoke-RestMethod 는 github.com 에 큰 파일(수십 MB)을 올리다
# "The underlying connection was closed" 로 끊기므로 Windows 에 들어 있는 curl.exe 로 (재시도 포함) 올림.
# 토큰은 명령줄에 보이지 않게 임시 헤더 파일로 넘기고 바로 지움. curl 이 없으면 Invoke-RestMethod.
function Send-ReleaseAsset([string]$UploadBase, [string]$File, [string]$ContentType, [string]$Name = '') {
    if (-not $Name) { $Name = [IO.Path]::GetFileName($File) }
    $uri = "$($UploadBase)?name=$([Uri]::EscapeDataString($Name))"
    Write-Host ("Uploading {0} ({1:N1} MB)" -f $Name, ((Get-Item -LiteralPath $File).Length / 1MB))
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if (-not $curl) {
        Invoke-RestMethod -UseBasicParsing -TimeoutSec 900 -Method Post -Uri $uri -Headers $headers -ContentType $ContentType -InFile $File | Out-Null
        return
    }
    $headerFile = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($headerFile, "Authorization: Bearer $env:GH_TOKEN`r`nAccept: application/vnd.github+json`r`nContent-Type: $ContentType`r`nExpect:`r`n")
        $output = & $curl.Source --silent --show-error --fail --http1.1 --retry 3 --retry-delay 5 --retry-all-errors --max-time 1200 `
            -X POST -H "@$headerFile" --data-binary "@$File" $uri 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Uploading $Name failed (curl exit $LASTEXITCODE): $((($output | Out-String).Trim()))" }
    }
    finally { Remove-Item -LiteralPath $headerFile -Force -ErrorAction SilentlyContinue }
}

$release = Send-Json Post "$api/releases" @{
    tag_name = "v$Version"; target_commitish = $env:GITHUB_SHA; name = "$Title v$Version"
    body = $notes; draft = $true; prerelease = $false
}
try {
    $upload = $release.upload_url -replace '\{.*\}', ''
    foreach ($file in $files) {
        $type = if ($file -like '*.zip') { 'application/zip' } else { 'application/octet-stream' }
        Send-ReleaseAsset $upload $file $type
    }
    $published = Send-Json Patch $release.url @{ draft = $false }
}
catch {
    $reason = $_.Exception.Message
    Write-Host "::error::Uploading or publishing the release failed; deleting the draft so the next run can retry: $reason"
    try { Invoke-RestMethod -UseBasicParsing -Method Delete -Uri $release.url -Headers $headers | Out-Null } catch { }
    throw
}
Write-Host "Released v$Version -> $($published.html_url)"
