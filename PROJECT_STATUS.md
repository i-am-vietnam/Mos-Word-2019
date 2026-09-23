# Project Status

Verified: 2026-09-23.

## Phase state

- Phase 0: complete. Independent Word repository, main branch, dedicated origin, protected Excel reference.
- Phase 1: complete. Five classic C# .NET Framework 4.7.2 projects in MosWord2019.slnx and a minimal WinForms shell.
- Phase 2: complete. Real Word lifecycle, explicit saving/discard, deliberate COM release, process ownership and deterministic disposal implemented and verified.
- Phase 3: complete. Word package models, structural loading/validation, build-copy contract, and Training working-copy service are implemented and verified without production task content.
- Phase 4+: not started. No Training UI workflow, grading, Testing Mode, database, installer, or release work.

## Current architecture

Core now owns Word-specific ProjectMeta, ProjectPackage, TaskDefinition, ValidationIssue and ValidationResult models. Projects owns deterministic Word2019_Pnn discovery, structural validation, language loading, and TrainingWorkspaceService. The sole production source root is MosWord2019.WinForms/Projects; classic MSBuild copies its content to the runtime Projects directory. No learner package exists yet.

IWordController defines IsOpened, StartWord, OpenDocument, Save, CloseDocument, Close and IDisposable. WordController owns one visible Word application and at most one .docx working copy. WordSession stores only owned state; WinApiProcessHelper captures and retains the verified process handle. WordGradingService remains an empty Phase 5 placeholder. The WinForms shell remains unchanged and does not invoke Word yet.

## Verification

Visual Studio MSBuild 18.10.1, Any CPU:

| Configuration | Errors | Warnings |
| --- | --- | --- |
| Debug | 0 | 0 |
| Release | 0 | 0 |

Real desktop environment: Office ProPlus2019Retail, x64, version 16.0.14026.20302; Word reports Version 16.0 / Build 16.0.14026.

The final disposable STA harness passed all 15 real-Word cases:

1. A: Start/Open/Close, including duplicate Start returning the same PID.
2. B: Edit/Save/Close, inspect saved OOXML, reopen in an independent Word instance and verify content.
3. C: CloseDocument/Open another in the same instance; unsaved changes discarded; Save without a document rejected.
4. D: External Word Quit, subsequent Save rejection, CloseDocument/Close safe.
5. External document Close, stale-state detection, Save rejection, reopen recovery.
6. E: Missing, invalid, empty, wrong-extension, starter, locked and read-only path rejection before activation.
7. Already-open controller document cannot be replaced; file held by another instance rejected.
8. Exception inside Documents.Open for an encrypted document: explicit error and no remaining owned process.
9. F: Repeated Close and Dispose; disposed controller cannot restart.
10. Save failure reproduced by making the backing file read-only: explicit failure, live content retained, cleanup succeeds.
11. Pre-existing PID ownership rejection.
12. Forced fallback operates only on the verified retained process handle.
13. Controller restart after external Word quit.
14. Extra externally opened document inside the owned application survives Close; intentional relinquishment is reported, then the harness closes its own extra document.
15. Wrong-thread calls rejected.

There are 15 named real-Word test cases in the final log (the grouped cases contain multiple checks). Word-unavailable activation was separately verified against the sandbox's absent COM registration, for 16 named cases overall. This did not uninstall or alter Office registration.

Initial real-Word WINWORD IDs: none. The final run kept sentinel PID 4832 with unsaved content alive throughout the controller tests. Final WINWORD IDs: none. No pre-existing user process was terminated. Earlier runs exposed an exit/termination race and an additional RPC-disconnection code; both were fixed before the final passing run. Save failure can leave Word read-only even after removing the backing-file flag; the test verifies preservation, not automatic save retry.

Build logs and final runtime evidence are retained under ignored artifacts/phase2/. Disposable harnesses and test documents are removed after verification. Phase 1 UI smoke evidence remains historical; no new visual/DPI acceptance is claimed.

Phase 3 verification used a disposable Word-created Word2019_P99 package outside the production root. Valid EN/VI loading, JSON extension data, Word-only deterministic discovery, display names, and all requested structural failures passed. First preparation copied the starter; second preparation preserved learner bytes; reset restored the starter; its SHA-256 hash remained unchanged. The Phase 2 controller opened, saved, closed and cleaned the prepared work.docx. Initial and final WINWORD PID sets were empty. The disposable package and harness were removed; artifacts/phase3-runtime.log and build logs remain ignored evidence.

Final Debug and Release builds each report 0 errors and 0 warnings. Both outputs contain Projects/README.md through the wildcard content rule and Newtonsoft.Json.dll. No Word2019_P01 production package was created.

## Git and reference safety

Word started Phase 3 clean on main at `c7bdf1affca9570d688b4e6dac7dc703ce7f1c7b` (`Initial MOS Word 2019 solution skeleton`), tracking origin https://github.com/i-am-vietnam/Mos-Word-2019.git. Phase 3 changes remain local and uncommitted. No commit or push was performed during this phase.

Excel's actual Git root is the parent; WinForms lives under ../MosTrainer. Reference HEAD: a75a89fc8a5502364e9b3b8b4eb9bc003d23a29d. Starting parent status: pre-existing untracked Installer/Output/MOS_Excel_2019_Setup_v1.0.0.rar and the separate MosWord2019/ folder. Final verification: all 185 tracked Excel files and the existing installer archive retained their hashes; parent HEAD, tracked diff and status are unchanged. No Excel file or parent Git configuration was edited. Do not edit parent Git settings or ignore rules.

## Next recommended task

Phase 4 — Training Mode foundation: Login -> language -> project selection -> working copy -> open Word -> task tabs -> Previous/Next -> Save/Close -> Restart. Do not implement grading yet.
