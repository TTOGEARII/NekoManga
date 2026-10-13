# NekoManga (=^･ω･^=)

흑백 만화에 마법을 부리는 올인원 데스크톱 앱.

- 💬 **번역 + 식자** — 말풍선을 읽고(OCR) 번역해 깔끔하게 다시 써넣기 · **앱 내장, 추가 설치 불필요**
  - 번역 엔진 선택: **Google**(무료) · **DeepL** · **DeepSeek** · **Grok**(xAI) — AI 엔진은 문맥을 살려 말투가 자연스러워요
- 🙈 **검열 제거** — 모자이크를 찾아 복원 · **기본**(내장 LaMa, 빠름) / **강력**(SDXL 재생성, ComfyUI 필요)
- 🎨 **자동 채색** — 흑백 원고를 AI(SDXL + ControlNet)가 풀컬러로 · **ComfyUI 필요**

번역·글자 지우기·기본 검열 제거는 **엔진이 앱에 내장**되어 있어요 (ONNX Runtime).
감지(comictextdetector) · 인식(manga-ocr) · 복원(LaMa) 모델(약 0.8GB)만 첫 실행 때 받으면 바로 쓸 수 있고,
외부 프로그램이 전혀 필요 없습니다.

자동 채색과 강력 검열 제거는 무거운 SDXL이 필요해 **ComfyUI(약 11GB)를 선택 설치**해요.
해당 기능 카드에는 "⚠ ComfyUI 설치 필요" 배지가 표시됩니다. 이미 설치해 둔 ComfyUI portable이 있으면 자동으로 찾아 연결합니다.

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
