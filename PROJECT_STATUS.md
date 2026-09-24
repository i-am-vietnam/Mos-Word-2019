# Project Status

Verified: 2026-09-24.

Build repair: Core now explicitly lists its ten source files, including exactly one Interfaces/IWordWindowLayout.cs entry. Namespace/import/Word-to-Core ProjectReference were already correct. Clean, standalone Core and Word builds, and full Debug/Release rebuilds each completed with zero errors/warnings. Visual Studio reloaded Core and successfully launched WinForms with F5; Login -> Training -> English -> Project 1 -> Go opened work.docx with eight tabs and upper/lower window placement. No UI, lifecycle or P01 source changes were made by this repair. Logs: artifacts/phase4b-repair.

## Phase state

- Phase 0: complete. Independent Word repository, main branch, dedicated origin, protected Excel reference.
- Phase 1: complete. Five classic C# .NET Framework 4.7.2 projects in MosWord2019.slnx and a minimal WinForms shell.
- Phase 2: complete. Real Word lifecycle, explicit saving/discard, deliberate COM release, process ownership and deterministic disposal implemented and verified.
- Phase 3: complete. Word package models, structural loading/validation, build-copy contract, and Training working-copy service are implemented and verified without production task content.
- Phase 4 including 4B: complete. Real P01 (eight tasks), bilingual Training entry, bottom-quarter task tabs, owned Word upper-area placement, internal save/close, switching, confirmed restart and manual closure recovery are verified. No Save buttons appear in the task panel.
- Phase 5+: not started. No grading, Testing Mode, database, installer, or release work.

## Current architecture

Core owns Word-specific package/validation models and lifecycle/layout contracts. Projects owns deterministic Word2019_Pnn discovery, structural validation, language loading, and TrainingWorkspaceService. The sole production source root is MosWord2019.WinForms/Projects; classic MSBuild copies its content to the runtime Projects directory without Link metadata or duplicate None entries. The supplied Word2019_P01 validates in EN/VI and appears as Project 1 with exactly eight tasks. Starter and language files are unchanged.

IWordController defines IsOpened, StartWord, OpenDocument, Save, CloseDocument, Close and IDisposable. WordController owns one visible Word application and at most one .docx working copy. WordSession stores only owned state; WinApiProcessHelper captures and retains the verified process handle. WordGradingService remains an empty Phase 5 placeholder. MainForm now orchestrates the existing package, workspace and Word services on the UI STA thread. Login permits Training only and stores en/vi in AppSession. No grading controls are exposed.

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

Phase 4B started clean on main at `c7ffaaa27f0cfe93914829fb39a385d2ff73667f`, tracking origin https://github.com/i-am-vietnam/Mos-Word-2019.git. Phase 4B changes remain local and uncommitted. No commit or push was performed.

Excel's actual Git root is the parent; WinForms lives under ../MosTrainer. Reference HEAD: a75a89fc8a5502364e9b3b8b4eb9bc003d23a29d. Starting parent status: pre-existing untracked Installer/Output/MOS_Excel_2019_Setup_v1.0.0.rar and the separate MosWord2019/ folder. Final verification: all 185 tracked Excel files and the existing installer archive retained their hashes; parent HEAD, tracked diff and status are unchanged. No Excel file or parent Git configuration was edited. Do not edit parent Git settings or ignore rules.

## Next recommended task

Phase 5 — Design Word grading architecture and the first small reusable assertion set against a real Word2019_P01 specification supplied by the user. Do not invent production MOS tasks.

## Current Phase 4B acceptance

- P01 EN/VI structural validation and discovery pass with eight supplied schema identifiers; no assertions are implemented.
- Real application Login -> Project 1 -> Go was observed with computer use. The task panel matches the Excel top-bar / tabs / footer concept; Save and Save/Close are removed, Grade and Testing absent. Normal switching/exit still save internally.
- At actual 125% desktop scaling, working area was (0,0,1920,1020), trainer (0,765,1920,255), owned Word (0,0,1920,765). Layout uses Screen.FromControl.WorkingArea and recalculates for display/work-area/font changes and after moving between screens. A font-relative minimum height protects small/high-scale displays.
- P01 real-Word checks passed: eight tabs, direct selection and button boundaries without reopening, persistence, Restart No/Yes, reset byte equality, saved exit, manual closure recovery, unchanged starter SHA-256 and final process-baseline restoration. A separate unsaved Word sentinel was neither moved nor closed; an attempted placement using its handle was rejected.
- EN/VI layout renders at 100/125/150% equivalent font sizes pass. Actual OS scaling was 125%; other OS scaling settings and multi-monitor hardware were not changed. The 150% equivalent uses a 300px minimum trainer height on this working area to keep instructions readable.
- Clean plus Rebuild Debug and Release: 0 errors, 0 warnings each. Visual Studio Error List was observed at 0 errors and 0 warnings, and Solution Explorer includes Projects/README.md normally.
- Evidence: ignored artifacts/phase4b runtime-final.log, layout.log, build/clean logs and form renders. Test working copies and harness binaries/source were removed. Excel tracked hashes and the supplied P01 starter/language hashes were unchanged.

## Phase 4 verification

Tests A-K passed in the disposable STA WinForms/real-Word harness: both Login languages, safe empty-root UI, injected discovery, Go opening work.docx, navigation and boundaries without reopening Word, save/switch/reopen persistence, Restart No, Restart Yes returning to Task 1, saved app exit and manual document/application closure recovery. An injected save failure cancels switching and exit without discarding the document. Missing runtime translations fail clearly; .docm packages are unavailable.

Focused Phase 2 open/save/close, repeated cleanup, manual closure and owned-process checks passed. Focused Phase 3 loader, EN/VI validator, invalid tasks, first copy, preserve, reset and starter SHA-256 checks passed. Initial and final WINWORD PID sets were empty; a separate unsaved sentinel remained intact during controller tests and was then closed by the harness. The first harness run had a sentinel cleanup error; the corrected final run had zero failures. Phase 2/3 source and grading were untouched.

Final Debug and Release rebuilds each have 0 errors and 0 warnings. EN/VI form renderings were inspected. Evidence remains in ignored artifacts/phase4; disposable fixture documents and harness sources/binaries were removed. Broad DPI/deployment acceptance is not claimed.
