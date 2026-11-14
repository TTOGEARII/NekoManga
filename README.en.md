# NekoManga (=^･ω･^=)

[한국어 README](README.md)

An all-in-one desktop app that works a little magic on black-and-white manga. Drag in your images and click a button to translate, remove censorship, and colorize your pages.

- 💬 **Translation and lettering** — NekoManga reads speech bubbles with manga-ocr, translates the text, and places it back in the bubbles.
  - Choose from **Google** (free), **DeepL**, **DeepSeek**, and **Grok**. The AI translation engines use context to produce more natural dialogue.
  - Text in vertical Japanese speech bubbles is laid out horizontally for Korean readability, with wrapping that keeps words intact.
- 🙈 **Censorship removal** — [Aletheia Lens](https://github.com/Cec1c/Aletheia-Lens) detects censorship bars and mosaics, with a dedicated model to help catch thin white bars. You can **review and correct the detection masks** before restoration.
  - After detection, use the review window to paint over missed areas or erase incorrect detections, then run restoration.
  - Choose from three restoration modes: **Basic** (fast restoration using Aletheia Lens’s DeepCreamPy), **Regeneration** (SDXL with a built-in workflow), or **Custom ComfyUI** (your own workflow).
  - Regeneration and Custom ComfyUI redraw **the entire area covered by each censorship mark**. The optional **Body Part Detection Model (부위 감지 모델)** (45 MB) identifies that area. If the model is unavailable or finds no match, the app expands the region by 0.6 times the size of the mark. You can adjust this under **Settings (설정) → Censorship Removal (검열 제거) → Advanced Options (고급 옵션)**.
  - Regeneration takes a 1024-pixel crop around each region and runs SDXL in two passes. The first reconstructs the censored patch and the second refines the whole area. This mode runs without LaMa. When the body part detector classifies a region as male or female, the app uses the corresponding tag set. Each set can be edited separately in Settings.
  - Masks you correct in the review window are saved as training data. You can use **semi-automatic training** in the app to adapt the detection model to your artwork.
- 🎨 **Automatic colorization** — Turn black-and-white pages into full-color artwork.
  - **Add a color cover as a reference** to match characters’ hair, eyes, and clothing to the cover.
  - Open ComfyUI from within the app and use workflows exported as API JSON for colorization or censorship removal.

NekoManga **installs, starts, and stops the AI engines automatically**. Translation and basic censorship removal use bundled models that require an initial download of about 1.1 GB. To use colorization, select the optional **colorization engine (about 20 GB)** during first-run setup.

Setup checks your PC, selects an engine for your graphics card, and provides download links for any required drivers or runtimes. The app also **adjusts resolution and memory settings automatically** based on your available VRAM and RAM.

## Installation

[Download the latest release](https://github.com/TTOGEARII/NekoManga/releases/latest), then run `NekoManga-win-Setup.exe`. The app notifies you when an update is available and lets you install it with one click.

### System requirements

| | Minimum (translation, lettering, and censorship removal) | Recommended (including colorization and regeneration) |
|---|---|---|
| OS | 64-bit Windows 10 | 64-bit Windows 10 or 11 (Windows 11 for AMD) |
| CPU | 4 cores or more | 6 cores or more |
| RAM | 8 GB | **32 GB** (below 16 GB, the app automatically disables engine preloading) |
| GPU | Optional | **NVIDIA RTX 20 series or newer** or **AMD Radeon RX 6000 series or newer**, with at least 8 GB of VRAM. GTX 10 series and older cards automatically use a slower legacy engine. |
| Disk space | 2 GB | **25 GB or more** |
| Internet | Initial model download (1.1 GB) | Initial model download (about 20 GB) |

- NVIDIA systems require driver **580 or later** for RTX 20 series or newer, or driver **528 or later** for GTX 10 series or older. They also require the Visual C++ 2015–2022 x64 runtime. Setup checks these requirements and provides download links when needed.
- AMD support uses the official portable ComfyUI ROCm build. AMD support is still in its early stages, so processing can be slower than on NVIDIA and some nodes may be unavailable.

On an RTX 2070 SUPER, typical processing times per page are about 8 seconds for translation and lettering, 2 seconds for basic censorship removal, and 2 minutes 30 seconds for automatic colorization.

Translation, censorship detection, and basic censorship removal automatically use DirectML GPU acceleration when at least 6 GB of dedicated VRAM is available and neither colorization nor GPU-based censorship removal is running. Otherwise, these tasks run on the CPU. You can change this under **Settings (설정) → Engine (엔진) → Built-in AI Acceleration (내장 AI 가속)**.

## Usage

1. Drag manga images or a folder into the main window. The app sorts filenames in natural order (`1`, `2`, `10`).
2. Turn on the features you want to use.
3. Click **✦ Start! (시작!)**. If censorship removal is enabled, the app scans each page. Pages with no detected censorship continue straight to colorization and lettering when those features are enabled. Pages with detections are marked **Pending Review (검토 대기)**.
   - Double-click a pending page to open the **Erase Censorship (검열 지우기)** tab. Red areas show what will be removed. Paint over missed marks, erase incorrect detections from the mask, and click **Run Censorship Removal (검열 제거 진행)**.
   - Click **Process All Pending Pages (대기 중 전부 진행)** to process the whole queue using the current masks.
   - To review all pages in a single window after detection, select **Review Window (검토 창)** under **Settings (설정) → Censorship Removal (검열 제거)**.
4. Find the output in `foldername_neko` next to the source folder. As each page finishes, its thumbnail and preview update to show the result.

### Translate manga into English

Open **Settings (설정) → Translation (번역)**. Under **Translation Language (번역 언어)**, select **English (영어)**. Then turn on **Translation (번역)** in the main window before clicking **Start**. The source text must be Japanese. This setting controls the language of the translated manga. The app’s controls are in Korean.

### Edit your results

Open **View and Edit Results (결과 보기·수정)** to make adjustments:

- 🙈 **Erase Censorship (검열 지우기)** — Edit the red mask for the current page using the brush (`B`), eraser (`E`), or polygon tool (`L`). Choose Basic, Regeneration, or Custom mode, then click **Erase (지우기)** to rerun censorship removal and lettering **for that page only**. If the page is already colorized, the tool works directly on the colorized image.
  - Edited masks are saved in `.neko` within the output folder and reused when you process the same source pages again. You can turn this off in Settings.
  - Zoom with `Alt` + mouse wheel or `Ctrl` + `+` / `-`, pan with `Space` + drag, and resize the brush with `[` or `]`.
- 💬 **Redo Lettering (식자 다시 하기)** — Edit text directly in its speech bubble. You can move it, resize it, or change its font and color.

In **Settings (설정)**, you can adjust colorization quality, censorship detection and restoration methods, the translation engine, and fonts. Under **Engine (엔진)**, you can load a replacement colorization model from a file or URL. If you move to a different PC, use **Tune for This PC (이 PC에 맞게 조정)** to recalculate the resolution and memory settings.

## For ComfyUI users

- Go to **Settings (설정) → Engine (엔진) → Open ComfyUI (ComfyUI 화면 열기)** to open the bundled ComfyUI in your browser. It shares models, nodes, and the processing queue with the app.
- Use **Add Built-in Regeneration Workflow to ComfyUI (내장 재생성 레시피를 ComfyUI에 넣기)** to copy the app’s censorship removal workflow into ComfyUI as a starting point.
- Save your edited workflow with **Export (API)**. You can then load it in **Custom ComfyUI** mode for censorship removal or as a custom colorization workflow.
  - Use one `LoadImage` node for input and one `SaveImage` node for output. NekoManga supplies the source page to `LoadImage`. For censorship removal, the image’s alpha channel contains the mask.

## Acknowledgments

- Censorship detection and basic restoration are based on [Aletheia Lens](https://github.com/Cec1c/Aletheia-Lens) (GPL-3.0). NekoManga ports its detection pipeline (hent-AI Mask R-CNN) and restoration pipeline (DeepCreamPy) to C# and downloads the model files published by that project.
- Thanks also to the projects it builds on: [hent-AI](https://github.com/natethegreate/hent-AI), [DeepCreamPy](https://github.com/cookieY/DeepCreamPy), and [Mask R-CNN](https://github.com/matterport/Mask_RCNN).

## License

[GNU GPL v3](LICENSE). Models and third-party components downloaded by the app are subject to their respective licenses.
