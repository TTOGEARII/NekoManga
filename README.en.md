# NekoManga (=^･ω･^=)

[한국어 README](README.md)

An all-in-one desktop app that works a little magic on black-and-white manga. Drag in your images and click a button to translate, remove censorship, and colorize your pages.

- 💬 **Translation and lettering** — NekoManga reads speech bubbles with manga-ocr, translates the text, and places it back in the bubbles. Text that sits on artwork or a black background is drawn with the text and outline colors swapped (white letters with a black outline) so it stays readable; turn this off under ⚙ Settings → 💬 Translation. Sound effects outside bubbles are left alone by default; choosing **Erase and replace with the translation (experimental)** in the same tab erases the original and draws the translation there in bold (glossary → built-in onomatopoeia dictionary → translator; tall sound effects are stacked vertically). Fixed terms such as character names go in the **glossary** (one per line, source=translation), and DeepSeek/Grok also receive the previous pages' dialogue as context.
  - Choose from **Google** (free), **DeepL**, **DeepSeek**, and **Grok**. The AI translation engines use context to produce more natural dialogue.
  - Text in vertical Japanese speech bubbles is laid out horizontally for readability, with wrapping that keeps words intact.
  - Only text inside speech bubbles is translated; sound effects and handwriting outside bubbles are left as they are (you can turn this off under **Settings → Translation**).
- 🙈 **Censorship removal** — [Aletheia Lens](https://github.com/Cec1c/Aletheia-Lens) detects censorship bars and mosaics, with a dedicated model to help catch thin white bars. You can **review and correct the detection masks** before restoration.
  - After detection, use the review window to paint over missed areas or erase incorrect detections, then run restoration.
  - Choose from three restoration modes: **Basic** (fast restoration using Aletheia Lens’s DeepCreamPy), **Regeneration** (SDXL with a built-in workflow), or **Custom ComfyUI** (your own workflow).
  - Regeneration and Custom ComfyUI redraw **the entire area covered by each censorship mark**. The optional **body part detection model** (45 MB) identifies that area. If the model is unavailable or finds no match, the app expands the region by 0.6 times the size of the mark. You can adjust this under **Settings → Censorship removal → Advanced options**.
  - Regeneration takes a 1024-pixel crop around each region and runs SDXL in two passes. The first reconstructs the censored patch and the second refines the whole area. This mode runs without LaMa. The first pass erases only the black bars inside each patch, so outlines and hands visible between the bars are kept. The first pass also follows the page’s line art lightly (default 0.25) so the new drawing connects with its surroundings. Adjust **Patch drawing CN strength** under **Settings → Censorship removal → Advanced options**: 0 ignores the line art and may not match the surroundings, while higher values tend to leave a flat white fill where the bar was. If you were still on an older version’s default (0 or 0.5), the first launch after updating moves it to 0.25; a value you set yourself is kept. If a piece still comes back as a flat white fill where the bar was, it is redrawn with a new seed up to twice and the least white result is kept (**Redraw flat-white pieces**, 0 turns it off). Body part tags are chosen per censored patch: **a region that contains only one sex gets only that sex’s tags**, and a region where both appear or the sex could not be determined gets both tag sets (for example, penetration scenes where the other side may be hidden, or patches where no body part was found). The progress line shows which tags are used, and patches the app could not classify can be set by hand in the results window. You can change this under **Settings → Censorship removal → Advanced options**, where an experimental option also applies each sex’s tags only to its own side of a mixed region.
  - Masks you correct in the review window are saved as training data. You can use **semi-automatic training** in the app to adapt the detection model to your artwork.
- 🎨 **Automatic colorization** — Turn black-and-white pages into full-color artwork.
  - **Add a color cover as a reference** to match characters’ hair, eyes, and clothing to the cover.
  - Open ComfyUI from within the app and use workflows exported as API JSON for colorization or censorship removal.

NekoManga **installs, starts, and stops the AI engines automatically**. Translation and basic censorship removal use bundled models that require an initial download of about 1.1 GB. If your graphics card qualifies, the engine for regeneration-based censorship removal (ComfyUI, about 12 GB) is downloaded as well. To use colorization, keep **Install colorization too (about 5 GB more)** checked during first-run setup.

Setup checks your PC, selects an engine for your graphics card, and provides download links for any required drivers or runtimes. The app also **adjusts resolution and memory settings automatically** based on your available VRAM and RAM.

## Installation

[Download the latest release](https://github.com/TTOGEARII/NekoManga/releases/latest), then run `NekoManga-win-Setup.exe`. The app notifies you when an update is available and lets you install it with one click.

### If the installer doesn't start

The installer isn't code-signed, so antivirus software or Windows may treat it as an unknown file and block or hold it.

- **"Windows protected your PC" appears**: select **More info**, then **Run anyway**.
- **Nothing appears after you run it**: your antivirus may be holding the file. First, end every `NekoManga-win-Setup.exe` process in Task Manager (Ctrl+Shift+Esc). Then allow the file in the antivirus notification or block history, or add it as an exception, and run it again.

All source code is public in this repository, and GitHub Actions builds the installer from that source.

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

Translation, censorship detection, and basic censorship removal automatically use DirectML GPU acceleration when at least 6 GB of dedicated VRAM is available and neither colorization nor GPU-based censorship removal is running. Otherwise, these tasks run on the CPU. You can change this under **Settings → Engine → Built-in AI acceleration**.

## Usage

1. Drag manga images or a folder into the main window. The app sorts filenames in natural order (`1`, `2`, `10`). If you add a folder you have already processed, the app skips the `foldername_neko` output folder inside it.
2. Turn on the features you want to use.
3. Click **✦ Start!**. If censorship removal is enabled, the app scans each page. Pages with no detected censorship continue straight to colorization and lettering when those features are enabled. Pages with detections are marked **Pending review**.
   - Double-click a pending page to open the **Erase censorship** tab. Red areas show what will be removed. Paint over missed marks, erase incorrect detections from the mask, and click **Start censorship removal**. All pending pages are then processed and the window closes.
   - Click **Process all pending** to process the whole queue using the current masks.
   - To review all pages in a single window after detection, select **Review window** under **Settings → Censorship removal**.
4. Find the output in `foldername_neko` inside the source folder. As each page finishes, its thumbnail and preview update to show the result.

### Translate manga into English

Open **Settings → Translation**. Under **Translation language**, select **English**. Then turn on **Translation** in the main window before clicking **✦ Start!**. The source text must be Japanese. This setting controls the language of the translated manga and is separate from the interface language.

### Interface language

The interface is available in Korean, English, and Japanese. On first run it follows your Windows display language (English unless Windows is set to Korean or Japanese). To change it, open **Settings → About → 🌐 Interface language**, or use the language box at the top right of the first-run setup window. The change applies right away.

### Edit your results

Open **View & edit results** to make adjustments:

- 🙈 **Erase censorship** — Edit the red mask for the current page using the brush (`B`), eraser (`E`), or polygon tool (`L`). Choose Basic, Regeneration, or Custom mode (the method from Settings is selected at first), then click **Erase** to rerun censorship removal and lettering **for that page only**. If the page is already colorized, the tool works directly on the colorized image.
  - To fix several pages, move through them and edit each mask (masks are saved automatically when you change pages), then click **Rerender all edited pages**. Only the edited pages are processed again in one batch, using the censorship removal method from Settings. The window closes and progress appears in the main list. You can repeat this as often as you like, or start the batch from the main window with **Rerender edited pages (N)**.
  - Pages drawn with Regeneration record the **seed** that was used. If you like a result, expand **Regeneration settings — this page only** and check **Lock this page's seed**. After that, **Erase**, **Rerender all edited pages**, and **✦ Start!** all redraw that page with the same seed. With the same mask and regeneration settings, you get the same image. Seeds are recorded only for pages that were processed through review.
  - In Regeneration mode, the mask view shows an **M/F badge** on each censored patch. The badge tells you which sex’s tags will be used to draw that patch. `?` marks a patch where no body part was found, so both tag sets are used. Click a badge to cycle through Male → Female → Both → Auto, or use **All male** or **All female** to set every patch at once. Your choices are saved with the page and used by **Erase**, **Rerender all edited pages**, and **✦ Start!**.
  - Patches that are not male also get a **Cum badge**. The creampie tags are no longer added to every female part — only where a patch is judged to be a creampie. If the automatic judgment (a tagging model) is wrong, click the badge to add or leave out the tags yourself (**Settings → Censorship removal → Advanced options**: *Creampie tags* and *Judge automatically*).
  - If a page has censored spots scattered in several places and the body around them comes out blurry or changed, try **Redraw censored spots that are apart separately (experimental)** in Settings — spots that do not touch are drawn on their own at close to the original size (it takes longer, one pass per spot).
  - Edited masks are saved in `.neko` within the output folder and reused when you process the same source pages again. You can turn this off in Settings.
  - The tool panel on the right is grouped into cards (View, Tools, Erase, Mask). Expand the **Guide** card at the top for the shortcut table: `B` brush · `E` eraser · `L` polygon · `[` `]` brush size · `Ctrl+Z` undo · `Ctrl+D` clear the whole mask · `Space` + drag to pan · `Alt` + wheel or `Ctrl` + `+` / `-` to zoom.
- 💬 **Edit lettering** — Work on the actual lettered page, much like a text layer in an image editor: click a bubble to select it, drag to move it, resize it with the eight handles, and rotate it with the round handle on top. Double-click to edit the text and its line breaks in place (Enter = new line), and use **＋ Add text** to put in sound effects or missed lines yourself. The selected bubble's original text (what OCR read) is shown above the translation box and also pops up when you hover a bubble, so you can fix a translation against the source. Both boxes grow to fit long text, and the bubble list shows "…" when an entry is too wide (hover to see the whole text). Font, size (px), color, outline (px), alignment, and rotation can be set for the selected bubble only, and editing one bubble leaves the other bubbles' text size unchanged. Ctrl+Z undo · Ctrl+Y redo · Delete hides the selected bubble. This tab’s **Guide** card lists the controls as well. To fix many pages at once, use **📑 Review sheet** in the main window: export the translations as CSV, edit the translation column in a spreadsheet, and import it back — only the changed pages are re-lettered. Apply with **Redo lettering**.

In **Settings**, you can adjust colorization quality, censorship detection and restoration methods, the translation engine, and fonts. Under **Engine**, you can load a replacement colorization model from a file or URL. If you move to a different PC, use **Tune for this PC** to recalculate the resolution and memory settings.

## For ComfyUI users

- Go to **Settings → Engine → Open ComfyUI screen** to open the bundled ComfyUI in your browser. It shares models, nodes, and the processing queue with the app.
- Use **Add built-in regeneration recipe to ComfyUI** to copy the app’s censorship removal workflow into ComfyUI as a starting point.
- Save your edited workflow with **Export (API)**. You can then load it in **Custom ComfyUI** mode for censorship removal or as a custom colorization workflow.
  - Use one `LoadImage` node for input and one `SaveImage` node for output. NekoManga supplies the source page to `LoadImage`. For censorship removal, the image’s alpha channel contains the mask.

## Acknowledgments

- Censorship detection and basic restoration are based on [Aletheia Lens](https://github.com/Cec1c/Aletheia-Lens) (GPL-3.0). NekoManga ports its detection pipeline (hent-AI Mask R-CNN) and restoration pipeline (DeepCreamPy) to C# and downloads the model files published by that project.
- Thanks also to the projects it builds on: [hent-AI](https://github.com/natethegreate/hent-AI), [DeepCreamPy](https://github.com/cookieY/DeepCreamPy), and [Mask R-CNN](https://github.com/matterport/Mask_RCNN).

## License

[GNU GPL v3](LICENSE). Models and third-party components downloaded by the app are subject to their respective licenses.
