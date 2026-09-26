# Technical Context

## Starting point

Independent MOS Word 2019 product. Read AGENTS.md and PROJECT_STATUS.md before work. Phase 0 through Phase 4B are complete; Phase 5 Project 1 grading is committed at `64c34c5f6468383e46c6262f824ab14b91188aaa`; Phase 6 P02 integration/grading and its focused correction are committed through `a969f5c592ab92b3e551c68a8242f114dd283f75`; the supplied P03 draft is committed at current HEAD `230e8bf2e62a313f83c6a90b82554232dc282785`. P02 T05 serialization hardening and Phase 7 P03 grading are complete locally and uncommitted. The roadmap is background scope for later work and the current user request controls the authorized phase.

Solution: MosWord2019.slnx, supported by local Visual Studio Community 18 / MSBuild 18.10.1. All five projects use classic MSBuild format, C# 7.3, .NET Framework 4.7.2, AnyCPU. WinForms outputs MosWord2019.exe. Use Visual Studio MSBuild, not an assumed dotnet build workflow:

Core explicitly lists its source files. New Core source files must be added to its Compile items; IWordWindowLayout.cs is included exactly once under Interfaces. Reload the project in Visual Studio after external project-file changes. This avoids stale IDE source-item evaluation hiding the interface while command-line wildcard builds succeed.

```powershell
& 'C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe' MosWord2019.slnx /t:Rebuild /p:Configuration=Debug '/p:Platform=Any CPU'
& 'C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe' MosWord2019.slnx /t:Rebuild /p:Configuration=Release '/p:Platform=Any CPU'
```

## Dependencies and responsibilities

- Core: no project references. IWordController defines the disposable document lifecycle. WordGradingService performs reusable OOXML grading and returns TaskGradeResult with Pass, Fail, or Error.
- Word -> Core. WordController implements the lifecycle; WordSession stores owned COM/process state; WinApiProcessHelper verifies ownership and performs bounded process cleanup.
- Projects -> Core. ProjectLoader, ProjectValidator and TrainingWorkspaceService implement package discovery, structure and file-copy responsibilities without Office COM.
- Data: no project references. Reserved library, no database implementation or dependencies yet.
- WinForms -> Core, Word, Projects, Data. MainForm orchestrates package loading, Training workspace preparation and the Word lifecycle. Core AppLogger writes best-effort infrastructure logs under LocalAppData/MosWord2019/Logs.

Word has a COMReference to Microsoft.Office.Interop.Word, type library GUID 00020905-0000-0000-C000-000000000046, version 8.7, WrapperTool primary, EmbedInteropTypes true, Private false. MSBuild resolves the installed Word 16.0 object library's PIA version 15.0.0.0. Office Core type library 2.8 is also referenced with embedded types, for AutomationSecurity. A development machine must provide the registered libraries/PIAs. Runtime and grading ground truth were verified on Office ProPlus2019Retail x64 16.0.14026.20302.

Core and Projects use Newtonsoft.Json 13.0.4 through packages.config and a repository-local ignored packages restore. SQLite/Dapper remain deferred until persistence is required. No references or linked source files point to Excel.

## Current shell flow

Program.Main (STA) -> Application.Run(LoginForm). LoginForm offers Training and EN/VI; Testing is described as unavailable and cannot be selected. Continue constructs an immutable AppSession and opens MainForm modally while Login is hidden; closing MainForm returns to Login. This is mode/language selection, not authentication. MainForm rejects AppMode.Testing.

AppSession is immutable instance-based mode/language state. MainForm owns the controller, current project, working path and task index. It uses a compact Excel-style top bar, localized TabControl and footer (<<, >>, Restart, Grade, status). Save/Close buttons are absent; saving is internal to switching, grading, and normal exit. There is no timer or Testing state. The constructor overload accepts a project root, workspace service, IWordController and optional dialog delegates for verification; normal construction always uses AppDomain.CurrentDomain.BaseDirectory/Projects and the standard Documents/MosWord2019 workspace.

Go validates selected translations, saves/closes existing work, prepares through TrainingWorkspaceService, opens only the returned work.docx and selects Task 1. Navigation only updates the instruction panel. Grade saves the live document, snapshots work.docx, and grades the selected tab without closing, reopening, resetting, or moving Word. Save failures route to a technical Error message and keep Word open. Confirmed Restart (default No) closes without saving, resets via the workspace service, reopens and selects Task 1. Manual document/application closure is detected through IsOpened; stale state is cleaned, a localized message is shown and Go can reopen saved work. Form close saves, closes and disposes on the same STA thread. Phase 2 ownership protection remains authoritative. Testing controls are absent.

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

Word owns its .git, main branch, and dedicated origin. Phase 5 P01 grading, Phase 6 P02 grading/corrections and the supplied P03 draft are committed. P02 T05 serialization hardening and complete P03 grading are local and uncommitted. The actual Excel Git root is the parent, not ../MosTrainer; never run a Git mutation in the parent. Parent status naturally lists MosWord2019/ as untracked. Do not hide that by modifying parent ignore rules. Reference/hash manifests are retained in ignored artifacts/.

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

## Phase 3 package architecture

The authoritative production source root is `MosWord2019.WinForms/Projects`. The classic WinForms project explicitly includes each supplied project file once with PreserveNewest, without Link metadata or duplicate build items, producing AppDomain.CurrentDomain.BaseDirectory/Projects at runtime. MainForm passes this root to ProjectLoader and filters to .docx. P01, P02 and P03 each have eight supplied tasks and display as Project 1, Project 2 and Project 3. Invalid packages are omitted; with none available, Go and task actions are disabled.

`ProjectMeta` contains ProjectId, OfficeVersion, Version and a Word default of `starter.docx`. `ProjectPackage` aggregates metadata, tasks, the selected language dictionary and an absolute package folder. DisplayName recognizes `Word2019_Pnn` and returns `Project n`. No Excel prefixes or workbook fields exist.

`TaskDefinition` contains only ProjectId, TaskId, TitleKey, InstructionKey and AssertionType. ProjectId is assigned from validated metadata and ignored during JSON serialization. JsonExtensionData stores arbitrary future assertion parameters in a case-insensitive `IDictionary<string, JToken>`. AssertionType must be nonempty for a task, but the validator does not call WordGradingService or maintain a fake support list.

ProjectLoader enumerates only top-level directory names matching case-sensitive `Word2019_P` plus at least two digits. It validates each directory, omits invalid packages, selects the exact requested EN or VI dictionary, resolves an absolute folder path and orders results by ProjectId with an ordinal case-insensitive comparer. There is no language fallback; a missing requested file is an error. GetText returns a selected translation or the key when absent.

ProjectValidator performs structural checks without Word: package directory; readable meta/tasks JSON; nonempty convention-compliant projectId exactly matching its folder; explicitly declared root-level starter; approved `.docx` or `.docm` extension; starter existence; nonempty task list; nonempty unique case-insensitive task IDs; title/instruction/assertion fields; lang directory; exact requested EN/VI file; and every title/instruction key in every present language file. It returns stable issue codes in ValidationResult. It deliberately does not inspect OOXML content or validate assertion support.

`TrainingWorkspaceService` owns file operations and never calls Word. Its production root is exactly `%USERPROFILE%\Documents\MosWord2019\Working`. An injectable root supports isolated verification. It validates package/project/starter paths, derives `work` plus the lowercase approved starter extension, and confines paths to the package and working roots. PrepareWorkingCopy creates the directory and copies only when work is absent. A second call preserves learner work. ResetWorkingCopy copies the starter to a same-directory temporary file and atomically replaces/moves the closed working file; the caller must close Word first. Copied files have only their read-only attribute removed. The starter is never written.

`.docx` and `.docm` are approved package/workspace extensions so extension handling does not require redesign. Phase 2 WordController currently accepts only `.docx`; `.docm` activation and macro policy remain explicitly deferred.

## Phase 3 verification handoff

The ignored disposable `Word2019_P99` fixture was created outside the production root, including a valid starter generated by Word Interop, one clearly nonproduction task with arbitrary deferred assertion text, EN/VI dictionaries and an empty assets directory. It was never returned by the production runtime root and was deleted with the harness after verification.

`artifacts/phase3-runtime.log` records passes for valid EN/VI packages, extension-data retention, missing/invalid meta, missing starter/tasks, duplicate task ID, missing requested EN/VI, missing language key, missing assertionType, invalid project ID, Word-only discovery, deterministic ordering, display names, selected language, resolved folder, first-copy creation, second-copy preservation, reset, unchanged starter SHA-256, exact default Training root, and Phase 2 open/save/close/process cleanup. Initial and final WINWORD sets were empty. Debug and Release logs show zero errors and warnings. Both outputs contain the copied production-root README and Newtonsoft.Json assembly.

At the Phase 3 checkpoint no production content or grading existed. Phase 4 added Training orchestration and Phase 4B integrated the supplied P01 without inventing instructions. Package/workspace services and lifecycle cleanup remain stable; later sections describe the P01/P02/P03 grader. Testing, score, timeout, database and installer remain unimplemented.

## Phase 4B layout and P01 contract

MainForm uses Segoe UI, WhiteSmoke, normal title bar and AutoScaleMode.Font. The top selector keeps a bounded width. Each tab uses the supplied TitleKey translation and a noneditable wrapping instruction label with scrolling on small displays. Clicking tabs and << / >> only affects UI state. Grade is absent. Normal close and switching save before closing; Restart defaults to No and explicitly discards only after Yes.

Screen.FromControl(this).WorkingArea is divided into the lower quarter for the trainer and the remainder for Word. A font-relative minimum height protects readability. Load, display/work-area notifications, font/DPI notifications and ResizeEnd recalculate placement. SystemEvents handlers are unsubscribed during disposal. The single TableLayoutPanel column uses 100% width so font scaling cannot expand it offscreen.

Core IWordWindowLayout is an optional placement contract separate from IWordController. WordController.SetWindowBounds obtains a short-lived Window from its held Document.ActiveWindow. WinApiProcessHelper.OwnedProcess.PositionWindow checks that its retained process handle is still alive and the HWND PID matches before restore/SetWindowPos (no Z-order/focus change). It never searches globally for an arbitrary Word window. The Window RCW is released in finally; no lifecycle ownership/cleanup rules were weakened.

P01 assertion identifiers are DocumentStyleSet, BulletedList, Footnote, HeaderDifferentFirstPage, SymbolInserted, PictureArtisticEffect, TableCellsMerged and PictureWrapType. Phase 5 implements all eight against generic TaskDefinition.Extra metadata.

## Phase 5 grading architecture

`WordGradingService` is a pure Core service. It copies a saved `.docx` into memory through a read-only FileStream with FileShare.ReadWrite/Delete, releases the source handle, and grades OOXML parts from the snapshot. It never creates or attaches to Word. `TaskGradeOutcome` separates Pass, Fail and Error so corrupt packages, missing metadata and unsupported assertions cannot appear as learner failures. `IsAssertionTypeSupported` returns true only for the 24 implemented P01/P02/P03 types.

## Phase 6 P02 grading architecture

P02 adds generic text removal/replacement, text-to-table, automatic TOC, text-box content, targeted comment deletion, exact paragraph spacing and whole-paragraph character-style assertions. They route by AssertionType and TaskDefinition.Extra metadata, never by P02 task ID. Ground truth from Word 2019 is retained under ignored `artifacts/phase6-p02` and `artifacts/phase6-bugfix`: T03 produces a 5-row, 3-column table. The grader checks the instructed columns, rows, cell content and order without requiring incidental table width/layout serialization. T04 is a Table-of-Contents SDT with `TOC \\o "1-3" \\h \\z \\u`; T05 uses the DrawingML Choice shape at CONTACT US with rect geometry and fill 002060 and permits automatic uppercase only through explicit metadata; T06 removes only the comment range on `proud`; T07 serializes 14 pt as line 280/exact; T08 applies the IntenseEmphasis character style to every visible run while tolerating run splitting and comment-reference runs without visible text.

DocumentStyleSet hashes a curated, normalized semantic set of document styles and ignores change records, rsids, relationship IDs and other volatile IDs. HeaderDifferentFirstPage requires current `w:titlePg`, no first-page header reference, and a normalized Integral primary-header structural signature. BulletedList resolves numId to abstractNum/level and checks the exact consecutive paragraph block plus effective 0/360-twip geometry. Footnote, SymbolInserted and TableCellsMerged require exact anchors/content/structure. Picture assertions resolve drawing relationships to media bytes and identify the target by SHA-256 rather than shape order. Pencil Sketch is `a14:artisticPencilSketch`; Square is `wp:anchor/wp:wrapSquare`.

Word 2019 ground-truth serialization came from disposable copies of the production starter. Lines (Stylish), Integral, Webdings 126 (`w:sym`/`F07E`), Pencil Sketch and Square were observed rather than inferred. DocumentStyleSet checks selected stable style-property paths and attributes from repeated apply/save/reopen samples, not a full style XML hash. HeaderDifferentFirstPage requires `titlePg`, an absent or semantically empty first-page header and the stable Integral table/fill/title-control structure in the primary header. Applying Pencil Sketch rewrites the source image to a deterministic PNG and adds an HD-photo layer, so T06 accepts the starter source fingerprint and the verified transformed fingerprint before checking the effect.

The P01 starter at Phase 5 blob `e7f2f69c12d7bf12464fc43c70b37916a052c40f` had SHA-256 `3D906B8A4C7E2DA3272DEC63A30E38CB5354651290A29B8E199BFAA223F83FED`. The current blob `910b3c2dd2bdaef958e76345707c67ae60ab873e` has SHA-256 `0A86091C0B58E39D7CDCC9D7F19919AA7182467794927FAEB11F620D75D151E4`; its meaningful task-state change is the T08 target wrap from Square to Tight. `styles.xml` is identical between the two blobs, so this history is unrelated to the T01 style-set false negative. Keep the current starter; never revert it as a grading workaround.

Ignored `artifacts/phase5` evidence covers fresh, correct, incorrect and near-miss documents, combined 8/8 grading, English/Vietnamese UI results, Save-error routing, unchanged Word PID during grade, layout, starter integrity and process cleanup. No artifact is part of the production project tree.

## Phase 7 P03 grading architecture

P03 adds eight generic assertions routed by AssertionType and TaskDefinition.Extra: TableFirstRowIsHeader, SectionOrientationByAnchor, TableColumnWidthsEqual, CitationPlaceholderAtParagraphEnd, SmartArtDirectionEquals, SmartArtAltTextDescriptionEquals, CorePropertyEquals and ParagraphFormattingMatches. Target selection uses semantic table rows, section content anchors, diagram relationships, exact core-property names and exact paragraph text. No assertion routes by ProjectId or TaskId.

Word 2019 ground truth shows: T01 Accessibility Checker reports `No header row (1)` for the table whose first row is `TOPIC|Quantity|Viewed|Feedback`; fixing it writes `w:tblHeader` on that row. T02 changes only the existing section containing `MOS Version` to landscape. T03 writes grid widths near 2261 twips for 1.57 inches without explicit row heights. T04 creates a citation SDT at the paragraph end with field `CITATION MOS \\l 1033` and a placeholder-only bibliography Source. T05 uses diagram data `dgm:dir val="rev"`. T06 writes `wp:docPr descr="Process flow"`. T07 is `docProps/core.xml` `cp:category`. T08 applies `IntenseEmphasis` to all visible paragraph runs plus justified paragraph/paragraph-mark formatting. Citation runs are excluded from T08's visible-body formatting check.

The supplied P03 starter is already in the requested T05 SmartArt direction, so fresh T05 grades Pass. This is a confirmed starter limitation and the starter was not changed to manufacture a failure. Wrong direction and changed SmartArt-data variants fail.

P02 T05 now treats one `mc:AlternateContent` as one logical shape: it prefers readable DrawingML Choice and uses the VML fallback only when Choice has no usable textbox. Text is concatenated across every `w:t`; exact mixed-case, mixed-case rendered with `w:caps`, and explicit uppercase are accepted only under the task's `allowAutomaticUppercase` metadata. Lowercase without caps, punctuation, extra text, wrong fill/geometry/anchor, body text and another textbox fail. The original reported failure used a stale pre-fix Debug binary; final production F5 loaded the rebuilt Core and returned Correct/Đúng before and after reopen/resave.

Ignored evidence under `artifacts/phase7-p03` contains Word-created ground truth, negative/near-miss variants, 8/8 combined grading, package/workspace checks, production F5 EN/VI results, switching, restart, layout and lifecycle logs. Ignored `artifacts/p02-t05-real` contains the exact P02 split-run/caps diagnostic and production F5 regression. Production starters were not modified.

Phase 4B evidence is under ignored artifacts/phase4b. Real P01 EN/VI validation, runtime navigation/persistence/restart/save/cleanup, starter hash and unrelated-window rejection passed. Actual desktop 125% working-area bounds: 1920x1020; trainer 1920x255 at y=765; Word 1920x765 at y=0. Equivalent 100/125/150% font-layout tests passed; this is not a claim of testing other OS display settings or multi-monitor hardware. Visual Studio showed README in Solution Explorer and 0 errors/0 warnings. Test working copies/harness binaries/source were removed.

## Phase 4 verification handoff

artifacts/phase4/runtime-final.log records zero failures across Tests A-K, focused lifecycle/package regressions, save-failure cancellation, missing runtime translation failure and .docm exclusion. The harness injected artifacts/phase4/fixtures/packages with Word2019_P98 and P99, Word-created starters, three explicitly non-MOS tasks and EN/VI dictionaries. Working files were also isolated under fixtures. Disposable fixtures and harness binaries/source were removed. Build logs and EN/VI form renders remain ignored. Initial/final Word PID sets were empty; a separate unsaved sentinel was preserved throughout the controller tests. The final corrected harness cleaned its own sentinel successfully. No production starter was edited or opened for learner work during verification.
