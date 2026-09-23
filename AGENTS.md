# MOS Word 2019 Agent Guide

Read AGENTS.md, PROJECT_STATUS.md, KNOWN_ISSUES.md, CODEX_CONTEXT.md, then only relevant source. The actual source wins over handoff documentation; the current user request controls scope.

1. MOS Excel 2019 is read-only architectural reference.
2. Never modify files or Git metadata in the MOS Excel repository. Its actual local Git root is the parent directory; its WinForms project is ../MosTrainer. Do not build or clean the reference as part of Word work.
3. All MOS Word development and generated files must remain under C:\Users\HUYNH HAU\source\repos\MosWord2019. Always run Git with this repository as the explicit working directory. Never mix histories, remotes, packages, or source references.
4. Preserve C# Windows Forms and .NET Framework 4.7.2.
5. Use Microsoft.Office.Interop.Word for Word desktop control.
6. Never edit starter.docx directly.
7. Training must use a working copy.
8. Testing must use SessionId-isolated working copies.
9. Never kill unrelated WINWORD.EXE processes; manage only proven session-owned processes.
10. New grading assertions require positive and negative verification, plus relevant regression checks. Never substitute fake PASS results.
11. Avoid broad refactoring or architecture changes without approval. Inspect relevant source and state, explain flow/files/protected areas, and make a small plan before significant changes.
12. Build Debug and Release with Visual Studio MSBuild after significant changes. Report all warnings.
13. Update PROJECT_STATUS.md after each completed phase.
14. Update CODEX_CONTEXT.md when architecture changes. Record only confirmed issues, constraints, and verification gaps in KNOWN_ISSUES.md.
15. Do not commit or push unless explicitly requested. Preserve existing changes. Expected Word origin: https://github.com/i-am-vietnam/Mos-Word-2019.git.

Implement only the phase authorized by the current user request. Lifecycle is Phase 2; packages Phase 3; grading Phase 5 onward. Do not inherit the Excel rule to build MosTrainer or reuse its GradingService in this independent product.
