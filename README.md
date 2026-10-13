# NekoManga (=^･ω･^=)

흑백 만화에 마법을 부리는 올인원 데스크톱 앱.

- 🎨 **자동 채색** — 흑백 원고를 AI(SDXL + ControlNet)가 풀컬러로
- 🙈 **검열 제거** — 모자이크를 찾아 LaMa로 자연스럽게 복원
- 💬 **번역 + 식자** — 말풍선을 읽고(OCR) 번역해 깔끔하게 다시 써넣기

ComfyUI와 BallonsTranslator를 엔진으로 쓰지만 **따로 설치할 필요가 없습니다** —
앱이 처음 실행될 때 필요한 구성요소만 자동으로 내려받아요.
이미 설치해 둔 ComfyUI portable / BallonsTranslator가 있으면 자동으로 찾아 연결합니다.

## 설치

[Releases](https://github.com/TTOGEARII/NekoManga/releases/latest)에서 `NekoManga-win-Setup.exe`를 받아 실행하세요.
설치 후 앱이 새 버전을 자동으로 감지해 업데이트 버튼을 띄웁니다.

### 요구 사항

- Windows 10/11 64비트
- NVIDIA GPU (VRAM 8GB 권장)
- 디스크 여유 공간 15GB (AI 모델)

## 사용법

1. 만화 이미지(또는 폴더)를 창에 끌어다 놓는다
2. 원하는 기능 토글을 켠다
3. **✦ 시작!** — 끝나면 원본 폴더 옆 `폴더명_neko`에 저장

세부 조절(채색 품질·모자이크 감지 민감도·번역 엔진 등)은 ⚙ 설정에서.

## 개발

```powershell
dotnet build NekoManga.sln
src\NekoManga.App\bin\Debug\net8.0-windows\NekoManga.exe
```

배포: `NekoManga.App.csproj`의 `<Version>`을 올리고 커밋 → `git tag vX.Y.Z && git push origin vX.Y.Z`
→ GitHub Actions가 Velopack 설치 파일을 빌드해 Release에 올린다.
