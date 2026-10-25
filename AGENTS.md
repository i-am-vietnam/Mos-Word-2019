# MOS Word 2019 Agent Guide

Read AGENTS.md, PROJECT_STATUS.md, KNOWN_ISSUES.md, CODEX_CONTEXT.md, then only relevant source. The actual source wins over handoff documentation; the current user request controls scope.

1. MOS Excel 2019 is read-only architectural reference.
2. Never modify files or Git metadata in the MOS Excel repository. Its actual local Git root is the parent directory; its WinForms project is ../MosTrainer. Do not build or clean the reference as part of Word work.
3. All MOS Word development and generated files must remain under C:\Users\HUYNH HAU\source\repos\MosWord2019. Always run Git with this repository as the explicit working directory. Never mix histories, remotes, packages, or source references.
4. Preserve C# Windows Forms and .NET Framework 4.7.2.
5. Use Microsoft.Office.Interop.Word for Word desktop control.
6. Never edit starter.docx directly.
7. Every new Training session must start from an untouched starter working copy. Preserve edits only within the active session; never resume saved answers on Go, switch or app relaunch.
8. Testing must use SessionId-isolated working copies.
9. Never kill unrelated WINWORD.EXE processes; manage only proven session-owned processes.
10. New grading assertions require positive and negative verification, plus relevant regression checks. Never substitute fake PASS results.
11. Avoid broad refactoring or architecture changes without approval. Inspect relevant source and state, explain flow/files/protected areas, and make a small plan before significant changes.
12. Build Debug and Release with Visual Studio MSBuild after significant changes. Report all warnings.
13. Update PROJECT_STATUS.md after each completed phase.
14. Update CODEX_CONTEXT.md when architecture changes. Record only confirmed issues, constraints, and verification gaps in KNOWN_ISSUES.md.
15. Do not commit or push unless explicitly requested. Preserve existing changes. Expected Word origin: https://github.com/i-am-vietnam/Mos-Word-2019.git.
16. When implementing a new project, do not edit completed older project packages or change their accepted grading behavior. An explicit user request to fix an older project or a shared lifecycle rule is a separate authorized scope; verify affected regressions.
17. Treat supplied question dictionaries (`lang/en.json`, `lang/vi.json`, or `vn.json` if supplied) as immutable exercise content. Do not rewrite, translate, expand or add workflow hints. Put implementation notes and verified workflow guidance in developer documentation/evidence instead. Restore changes only when explicitly requested and identify the baseline.
18. Stage packaged assets into Documents through the existing safe service. On normal project/session close, including normal application exit, remove only unchanged copies created by that service in the current session. Preserve pre-existing, modified, replaced, locked and unrelated personal files; log cleanup failures. Additional configured Pictures copies follow the same ownership safeguards. Never edit packaged assets or infer ownership from filename/hash alone.

Implement only the phase authorized by the current user request. Lifecycle is Phase 2; packages Phase 3; grading Phase 5 onward. Do not inherit the Excel rule to build MosTrainer or reuse its GradingService in this independent product.
