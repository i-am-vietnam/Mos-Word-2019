# MOS Word 2019 Roadmap

## Scope and initial checkpoint

At task start: Phase 0 — in progress; Phase 1 — in progress; Phase 2+ — not started.
The final phase state is recorded in PROJECT_STATUS.md and the completion checkpoint below.
Source: supplied RoadMap2019 MOS Word.txt and the user's Phase 0 + Phase 1 request. The request takes precedence: configure a safe Interop reference now if available, but no lifecycle implementation.

Independent product: C# Windows Forms, .NET Framework 4.7.2, Word 2019 desktop, Microsoft.Office.Interop.Word; Newtonsoft.Json for future JSON; SQLite/Dapper only when needed; Inno Setup later; Windows 10/11. No framework or UI migration without explicit authorization.

| Phase | Scope |
| --- | --- |
| 0 | Isolate Word workspace/Git; protect Excel reference; handoff documentation |
| 1 | Five-project solution, shell, placeholders, Debug/Release builds |
| 2 | Word startup/open/save/close/quit, COM release and owned-process lifecycle verification |
| 3 | Folder packages: metadata, tasks, starter.docx, EN/VI language, optional assets |
| 4 | Training without grading: selection, working copy, navigation, save/restart/close |
| 5 | Word grading architecture based on final document state |
| 6 | Reusable assertions: text, font, paragraph, styles, page/section, lists, tables, headers/footers, images/shapes, properties; advanced assertions only when tasks require them |
| 7 | Complete Word2019_P01 with positive, negative, near-miss verification |
| 8 | Training grading through WordGradingService |
| 9 | Additional packages and assertion regression |
| 10 | Testing Mode: seven projects, fixed selected order, 50 minutes, no per-task grading |
| 11 | Session identity, isolated copies, authoritative UTC deadline |
| 12 | Shared manual/timeout submission; grade unvisited untouched copies too |
| 13 | Equal-project-weight decimal score: 1000 * sum(passed/total per project) / 7; non-perfect display below 1000 |
| 14 | Results: score and incorrect task IDs; diagnostics remain internal |
| 15 | Timeout locks UI and submits without confirmation |
| 16 | Restart only current project; preserve session/order/index/deadline |
| 17 | Word package validator, supported assertion routing, language keys |
| 18 | Positive/negative/near-miss regression matrix |
| 19 | COM lifecycle and failure-path regression; no owned process leaks |
| 20 | Independent best-effort logging |
| 21 | Full branding and UI polish |
| 22 | New Inno Setup installer and distinct AppId |
| 23 | .NET and Word desktop prerequisite checks |
| 24 | Release candidate: builds, validation, EN/VI flows, submission, timeout, cleanup, install/uninstall |
| 25 | Classroom acceptance on Windows 10/11 and Office 2019 |

Future runtime data: Documents/MosWord2019/Working/<ProjectId>/work.docx and Documents/MosWord2019/Testing/<SessionId>/<ProjectId>/work.docx; logs under LocalAppData/MosWord2019/Logs. These are future product paths, not permission to create runtime data during Phase 1.

Milestones: M1 includes verified Word lifecycle (not achieved by the Phase 1 shell alone); M2 Training P01; M3 stable reusable grading; M4 Testing ready with at least seven validated packages; M5 release candidate.

No Excel source/controller/grader/assertion/package/starter/installer is copied. No Word package, database, timer, submission, scoring, installer, or release is implemented in Phase 1.

## Completion checkpoint — 2026-09-23

Phase 0 — complete. Phase 1 — complete. Phase 2 — complete. Phase 3+ — not started.
Debug and Release each built with 0 errors and 0 warnings. Shell smoke checks and the Phase 2 real-Word lifecycle matrix passed. M1 lifecycle milestone is achieved on the verified local Office 2019 x64 environment; wider deployment acceptance remains later work.
