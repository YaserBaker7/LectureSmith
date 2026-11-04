# 🎓 LectureSmith

[![Language](https://img.shields.io/badge/Language-C%23-blue.svg)](https://dotnet.microsoft.com/en-us/languages/csharp)
[![Framework](https://img.shields.io/badge/Framework-.NET%209.0-purple.svg)](https://dotnet.microsoft.com/download)
[![UI Framework](https://img.shields.io/badge/UI-Avalonia%20UI%2011.3-orange.svg)](https://avaloniaui.net/)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-blue.svg)](#)
[![Version](https://img.shields.io/badge/Version-v3.5.1-success.svg)](https://github.com/YaserBaker7/LectureSmith/releases)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

**LectureSmith** is a modern Windows desktop application designed for students and educators. It transforms lecture slides (PDFs) and reference textbooks into comprehensive, beautifully formatted study notes using **Google AI Studio (Gemini API)**.

Upload lecture slides, optionally attach reference textbook chapters, choose your study mode, and export directly to **Obsidian Markdown**, **self-contained HTML**, or **printable PDF**.

---

## 🚀 Key Features

* **Smart Note Generation:** Two tailored modes — **Missed Lecture** (in-depth, professor-style explanations) and **Attended Lecture** (concise, high-yield revision).
* **Multi-Modal Ingestion:** Supports raw PDF text extraction, native OCR scanning (Tesseract), and full multimodal Vision AI.
* **Textbook Context Integration:** Attach reference textbook chapters/pages to ground notes in official academic literature.
* **Interactive Slide Skipping:** Preview thumbnails and selectively exclude syllabus, intro, or administrative slides.
* **Seamless Slide Rewind:** Automatically recovers from interruptions mid-deck, rewinding incomplete slides and resuming cleanly.
* **History & Follow-up Q&A Studio:** Browse past generation sessions, review full notes, and chat interactively with an AI study assistant.
* **Multi-Format Export:** Export to Obsidian Notes (`.md`), self-contained single-file HTML (`.html`), or high-fidelity printable PDF (`.pdf`).
* **Modern UI & Theming:** High-contrast Dark and Light modes, real-time token tracking, and responsive bento analytics.

---

## 🛠 Tech Stack

| Component | Technology |
|---|---|
| **Runtime & Language** | C# / .NET 9.0 |
| **Desktop UI** | Avalonia UI 11.3 (MVVM with CommunityToolkit.Mvvm) |
| **PDF Extraction** | Docnet.Core & SkiaSharp |
| **OCR Scanner** | Tesseract OCR 5.2 |
| **Markdown Processor** | Markdig |
| **PDF Generation** | Headless Browser Engine (Edge/Chrome) + QuestPDF Fallback |
| **AI Integration** | Mscc.GenerativeAI (Google Gemini API) |

---

## 🚀 Download & Installation

### Windows Setup Installer (Recommended)
Download the latest Windows installer (`LectureSmith-Setup-v3.5.1.exe`) from [GitHub Releases](https://github.com/YaserBaker7/LectureSmith/releases).
* **Self-Contained:** No .NET runtime or SDK installation required.
* **Standard Installer:** Includes Start Menu shortcuts, optional desktop icon, and clean Windows uninstallation.

### Build from Source
1. Install [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).
2. Clone and run:
   ```bash
   git clone https://github.com/YaserBaker7/LectureSmith.git
   cd LectureSmith
   dotnet restore
   dotnet run
   ```

### Building the Setup Installer (.exe)
```powershell
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true --self-contained true
iscc installer.iss
```
The installer will be generated at `Output/LectureSmith-Setup-v3.5.1.exe`.

---

## ⚙️ Quick Setup

1. **Get an API Key:** Obtain a free API key from [Google AI Studio](https://aistudio.google.com/).
2. **Configure:** Open **Settings (⚙)** in LectureSmith, paste your key, and click **Save & Validate** (or set environment variable `GEMINI_API_KEY`).
3. **Generate:** Upload your slides, choose course & format, and click **Generate Notes**.

---

## 📄 License
This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
