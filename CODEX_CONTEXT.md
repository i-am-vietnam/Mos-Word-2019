# Technical Context

## Starting point

Independent MOS Word 2019 product. Read AGENTS.md and PROJECT_STATUS.md before work. Phase 0, Phase 1 and Phase 2 are complete; Phase 3+ are not started. The attached roadmap is background scope for later work, not authorization to implement those phases now.

Solution: MosWord2019.slnx, supported by local Visual Studio Community 18 / MSBuild 18.10.1. All five projects use classic MSBuild format, C# 7.3, .NET Framework 4.7.2, AnyCPU. WinForms outputs MosWord2019.exe. Use Visual Studio MSBuild, not an assumed dotnet build workflow:

```powershell
& 'C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe' MosWord2019.slnx /t:Rebuild /p:Configuration=Debug '/p:Platform=Any CPU'
& 'C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe' MosWord2019.slnx /t:Rebuild /p:Configuration=Release '/p:Platform=Any CPU'
```

## Dependencies and responsibilities

- Core: no project references. IWordController defines the disposable document lifecycle. WordGradingService remains an empty Phase 5 placeholder with no grading behavior.
- Word -> Core. WordController implements the lifecycle; WordSession stores owned COM/process state; WinApiProcessHelper verifies ownership and performs bounded process cleanup.
- Projects -> Core. Reserved library, no loader/validator/package schema yet.
- Data: no project references. Reserved library, no database implementation or dependencies yet.
- WinForms -> Core, Word, Projects, Data. These references establish the requested shell structure; no engine is invoked.

Word has a COMReference to Microsoft.Office.Interop.Word, type library GUID 00020905-0000-0000-C000-000000000046, version 8.7, WrapperTool primary, EmbedInteropTypes true, Private false. MSBuild resolves the installed Word 16.0 object library's PIA version 15.0.0.0. Office Core type library 2.8 is also referenced with embedded types, for AutomationSecurity. A development machine must provide the registered libraries/PIAs. Phase 2 runtime was verified on Office ProPlus2019Retail x64 16.0.14026.20302. No assertions exist.

No NuGet packages are needed yet. Add Newtonsoft.Json with package models later; SQLite/Dapper only if persistence is required. No references or linked source files point to Excel.

## Current shell flow

Program.Main (STA) -> Application.Run(LoginForm). LoginForm offers Training/Testing and EN/VI. Continue constructs an immutable AppSession and opens MainForm modally while Login is hidden; closing MainForm returns to the same Login. This is an entry shell, not authentication. There are no copied credentials. MainForm explicitly states the selected mode is unavailable; its notice and Back button support EN/VI. Full UI localization is deferred.

AppSession is instance-based selected mode/language state, not an exam session. The shell has no timer, SessionId, working-copy creation, persistence, grading result, or fake success behavior. The separate Word engine now handles a caller-supplied working-copy path. Empty scaffold directories are not tracked by Git; future phases create files as needed.

## Excel inspection and reuse classification

Reference documentation: parent AGENTS.md, PROJECT_STATUS.md, KNOWN_ISSUES.md and CODEX_CONTEXT.md. Inspected MosTrainer/MosTrainer.slnx, classic Core/Excel/WinForms project configuration, Program, LoginForm, AppMode/AppSession, Form1 Training open flow and Testing orchestration, model/validation/logging source, ProjectLoader/ProjectValidator, Data initializer/repository, ExcelSession and controller/grader entry points, Testing session/factory/state/result/reason/score source.

Excel flow: Program.Main -> LoginForm -> Form1. Training uses ProjectLoader.LoadAll -> ProjectValidator.Validate, OpenProjectExcel working copy, GradingService.CheckTask -> IExcelController/ExcelController. Testing uses TestSessionFactory.Create, fixed seven-project session/UTC deadline, session-isolated workbooks, shared manual/timeout submission, equal-project-weight decimal scoring. Data persistence is a prototype not invoked by the reference UI.

| Candidate | Later adaptation requirement |
| --- | --- |
| AppMode, AppSession | Generic mode/language concept; Word shell written fresh with immutable instance session |
| ProjectMeta | Replace starter.xlsx default with starter.docx |
| ProjectPackage | Replace Excel prefixes/display-name assumptions with Word2019_P |
| TaskDefinition | Retain generic identity/localization/extension-data concept; remove Excel sheet/cell/formula/chart fields; design real Word contract in its phase |
| ValidationIssue, ValidationResult | Generic issue collection; result depends on future Word package/task models |
| AppLogger | Adapt product-specific LocalAppData folder and file names |
| ProjectLoader | Adapt Word package schema/filenames; dependency on ProjectValidator prevents blind copy |
| TestSession, TestSessionFactory | Generic fixed-order, identity, UTC deadline, eligibility/shuffle concepts; depend on Word package models |
| TestProjectState | Replace WorkingWorkbookPath and workbook-specific workspace behavior |
| TestTaskResult, TestProjectResult, TestSubmissionResult, TestSubmissionReason | Generic result hierarchy/reason; dependencies on session/scoring must be adapted together in later phases |
| TestScoreCalculator | Pure decimal algorithm; depends on result models and RequiredProjectCount; deferred |

Never copy IExcelController, ExcelController, ExcelSession, Excel GradingService/assertions, task packages, starters or installer as implementation. ProjectValidator's assertion support is coupled to the Excel grader and must be independently designed for Word. No source files were copied; only small architectural concepts informed the fresh shell.

## Repository safety

Word owns its .git, main branch, and dedicated origin. The actual Excel Git root is the parent, not ../MosTrainer; never run a Git mutation in the parent. Parent status naturally lists MosWord2019/ as untracked. Do not hide that by modifying parent .gitignore or .git/info/exclude. Initial reference state and hash manifest are retained in ignored artifacts/. No commit/push was made.

## Phase 2 lifecycle contract and ownership

IWordController is the only Core-facing lifecycle contract. It extends IDisposable and exposes IsOpened, StartWord, OpenDocument, Save, CloseDocument and Close. WordController must be created, called and disposed on one STA thread. Construction alone never starts Office. OwnedProcessId is a diagnostic property on the concrete controller; no COM references or process handles are public.

StartWord reuses a live session. After manual Word closure it cleans stale state before a new activation. It uses new Microsoft.Office.Interop.Word.Application, never GetActiveObject. This PIA does not expose Application.Hwnd. The verified alternative is:

1. Snapshot existing WINWORD IDs as an exclusion set and record activation time.
2. Activate a new Application, give its empty window a GUID caption and make it visible.
3. EnumWindows finds the exact caption on class OpusApp; GetWindowThreadProcessId yields the PID.
4. Reject a pre-existing PID; open a process handle with query, synchronize and terminate rights; verify WINWORD.EXE image and creation time.
5. Retain that kernel handle and restore the original caption. The process handle, not a later PID lookup, is the only termination target. PID reuse cannot redirect cleanup.

An explicitly rejected pre-existing ownership match is released without Quit or native termination. Other startup failures attempt cleanup of the newly activated COM instance; native fallback is impossible without a verified handle. The process-name enumeration is only for exclusion, never a list to kill.

Application.Caption is a documented writable Word property: https://learn.microsoft.com/en-us/office/vba/api/word.application.caption. The exact empty-window behavior and PID association were also checked against the installed Word before implementation.

WordSession contains Application, Document, the opening document path, and the verified owned process handle. It contains no lifecycle methods, grading or shared/global state.

OpenDocument refuses an already-open controller document, validates the path/extension/existence/write access and literal starter.docx name, then opens only the existing working copy. An exclusive file-access probe rejects files held by another process before activation. Documents.Open disables conversion/encoding prompts and recent-file updates, and does not request repair or replacement creation. Unknown nonempty passwords ensure encrypted files fail rather than prompting. AutomationSecurity disables document macros for the owned application. ReadOnly is checked again after open to catch a race. Open failures clean the session and retain the underlying exception in the reported error.

Save never implies Close and never creates a new document. It rejects a missing/manually closed or read-only document, invokes Document.Save and checks Saved. Infrastructure errors preserve the session for recovery; there is no automatic discard after Save failure.

CloseDocument uses wdDoNotSaveChanges on only the held Document. Save first when persistence is intended. Normal/stale-document close releases the Document RCW and clears its path; unexpected close errors are explicit and keep the reference available for retry.

Close first checks for documents not owned by the controller. It then attempts the owned document close/release and, when safe, Application.Quit(wdDoNotSaveChanges) followed by Application RCW release. Documents collections are short-lived local RCWs, released immediately after inspection/open; none is retained in the session. All COM release is deliberate via Marshal.FinalReleaseComObject; no GC loops or COM finalizer are used.

After COM release, Close waits up to three seconds on the verified process handle. Only if needed does it call TerminateProcess on that same handle and wait up to three seconds. If termination races with an already-started natural exit, it waits for the exit before reporting failure. Unexpected cleanup errors are collected and surfaced after other cleanup attempts; a failed process cleanup retains the handle for retry. Successful Close is repeatable, and the controller can start a new session until Dispose succeeds. Dispose invokes Close; repeated Dispose/Close is safe on the creating STA thread.

If an extra document exists, or COM inspection cannot establish that remaining documents are owned, Close does not Quit/terminate the application. It releases its references/handle and reports that Word was deliberately left for the user. The harness verified preservation of an extra unsaved document and then closed that harness-created document itself.

Recognized RPC disconnection/server-exit and Word deleted-document HRESULTs are treated as expected stale state during cleanup/IsOpened. Save still fails explicitly when the document is unavailable. Other COM failures are not silently swallowed.

## Phase 2 verification handoff

Final real-Word evidence: artifacts/phase2/runtime-run3.log. Fifteen named cases cover the six requested A-F scenarios plus duplicate open, independent persistence, locked/read-only inputs, encrypted-open exception, save failure, stale-session restart, wrong-thread access, pre-existing PID rejection, verified-handle forced cleanup, and extra-document protection. Every case checked that a separate unsaved sentinel document remained intact. Initial/final WINWORD sets were empty; all harness-created processes were closed. The separate sandbox COM-unavailable case passed. Final Debug and Release builds each had zero warnings and errors.

The disposable harness accessed private session state only to edit/externally close its own test objects; production code does not expose these internals. Disposable documents and harness binaries/source were removed after verification; logs and hash manifests remain ignored. No production starter or task package was created. Phase 1 UI source, grading placeholder, Excel source/packages/installer and parent Git metadata were not changed.
