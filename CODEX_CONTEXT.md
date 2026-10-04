# Technical Context

## Starting point

Independent MOS Word 2019 product on main HEAD f1615f23fe24610f781f0aebb7988a508ed10eb9. P01-P07 including P06 corrections are committed; current P08 implementation is local/uncommitted. Current source controls behavior. Testing Mode is not implemented.

## Project 8 package and saved-state grading

P01-P08 validate/load with eight tasks each in EN/VI; production roots and build-copy strategy are unchanged. Core now explicitly includes Services/WordGradingService.DocumentAssertions.cs. There are 53 supported identifiers. P08 reuses Footnote, TableAccessibilityFirstRow, TextRangeFormattingEquals and CommentResolved, and adds DocumentMarginsEquals, TableCellSpacingEquals, PictureBorderColorEquals and TrackedChangesDisposition. No ProjectId/TaskId branches or grading COM were added.

T01 references the first paragraph immediately after OVERVIEW, immediately after sources and before the final period; unchanged full paragraph and exact Free resources footnote text are checked. Word's leading note-marker delimiter retains the existing TrimStart contract. Optional referenceAfterText and precedingHeading preserve P01's original end-of-heading default. Missing user-footnote parts return Fail; duplicates cannot throw SingleOrDefault.

T02 checks all active sections' top/bottom=1080 and left/right=720 twips; historical sectPrChange is ignored. Mirror/book-fold layouts fail; header/footer distances and gutter are irrelevant.

T03 ground truth is native No header row -> Use first row as header (first), then Mark as layout table (second), targeting the exact No./Categories/Quantity/Viewed/Responses table. Current tblLook is val=04A0/firstRow=1, compared with baseline 0480/firstRow=0; this differs from Repeat Header Rows. Later F5 Checker did not reproduce the original error; see KNOWN_ISSUES and do not claim that UI repetition passed.

T04 unchanged complete MOS is the only justification... paragraph resolves to Calibri 12pt, complex-script 11pt, no effective bold/italic/explicit color, inherited left alignment. Optional expectedAlignment resolves paragraph-style inheritance without changing P06's contract. Split runs pass.

T05 native Table Options Allow spacing 0.02 inch saves tblCellSpacing w:w=14/type=dxa on table and rows; reopened dialog remains 0.02 inch and COM Table.Spacing=1.4pt. Do not substitute the mathematical full-gap 29 twips or cell margins. Original table content must remain unchanged.

T06 preserves the MOS 2013 (Office 2013) range/comment and maps comments.xml to commentsExtended.xml done=1. Exact comment text is MOS 2013 suite[NBSP]retired on July 13. 2023.

T07 page-1 laptop picture anchored to Welcome to website... is fingerprinted as 63CEBB5CBC338E97A6F27A29C012C2718D9ECB691BFC7BD34EE186430B9C18DA. Native Blue, Accent 1, Darker 25% produces a:ln/a:solidFill/a:schemeClr val=accent1 with a:lumMod val=75000; theme accent1=4472C4, observed effective RGB=2F5497. Adding color to starter No Outline adds Word's default solid 0.75pt line (width omitted/default 9525 EMU), not an invented pre-existing visible line. Geometry/anchor/wrap are preserved; effect extents may change with the actual new outline.

T08 Word reports two revisions: insertion of The following table shows the inspection results – update Oct 2023: plus paragraph mark, and deletion of The following table shows the results of article statistics by topic published on the website http://www.hocict.edu.vn. XML contains two ins nodes/one del node and no PrChange. The assertion rejects pending revisions and checks unchanged non-generated body paragraphs, tables and comment; optional rejectedFormattingRanges/rejectedMargins verify real tracked-format/layout fixtures. TOC generated results are excluded; unrelated SDT text is included. Actual combined handles old text revisions first and disables further tracking before other edits.

P08 starter SHA-256: E1D8C71849C49B16243376FDFEFC0576F69283D4AE38F7705E19DFFB2F623776. All P01-P08 starters remain immutable. Combined P08 8/8 and P01-P07 8/8 regression verified; UI/workspace tests and limitations are in PROJECT_STATUS.md. Evidence is ignored under artifacts/phase12-p08. No lifecycle, MainForm, template cleanup, package inclusion or earlier task metadata changed.

## Established package grading and legacy Training

P01–P07 have eight tasks each, EN/VI validation 0/0 and deterministic discovery. WordGradingService has 49 supported identifiers. Existing P01–P05 task metadata remains unchanged. Six new generic identifiers are FileExistsInCustomOfficeTemplates, ModernWordDocumentFormat, HeaderTextEffectEquals, BookmarkAtParagraphStart, TableOfContentsLevels and FootnotesConvertedToEndnotes. P07 also reuses PictureWrapType and BodyTextReplaceAll; the latter has optional expectedBodyParagraphs metadata without changing its P06 contract. New pure OOXML code is in Core/Services/WordGradingService.LegacyProjectAssertions.cs.

P06 border expectation is confirmed Red, Accent 1: themeColor=accent1, effective B71E42, single Box edges, sz=24 and whole-document scope. Theme/effective color are both checked. P06's other assertions retain their established contracts, including logo source-layer fingerprints, custom-bullet font/glyph, property, split-run replacement, owning-part image hyperlink and inherited clear-format semantics.

P07's supplied starter is legacy .doc. ProjectValidator and TrainingWorkspaceService allow .doc alongside .docx/.docm package inputs; MainForm and WordController expose only .docx/.doc Training runtime. Macro AutomationSecurity remains ForceDisable. Starter-name, read-only, lock, path and process-ownership guards remain intact. Production starter.doc is never converted or edited.

PrepareWorkingCopy copies legacy starter.doc to work.doc until a learner-created work.docx checkpoint exists. Real Word Convert initially changes CompatibilityMode 11 to 15 while FullName remains .doc; Save changes FullName to work.docx, SaveFormat 12. IWordDocumentState reads the held document's live FullName. PreserveSavedWorkingCopy uses exact work.docx for converted legacy projects and nonmacro Save As checkpoints, never OOXML bytes in work.doc. Restart closes without saving, restores binary work.doc, deletes only that workspace's exact work.docx checkpoint and returns Task 1. Switch/reopen preserves the modern checkpoint. No automatic conversion or second Word application is used for grading. Binary .doc T01 fails; other OOXML tasks return a technical Convert-first message.

P07 ground truth: target default header MO-100: MICROSOFT WORD (OFFICE 2019), effective 4472C4 and w14:shadow blurRad=38100/dist=25400/dir=5400000/algn=ctr, color 6E747A/alpha=57000. The part/relationship ID is resolved dynamically. Author picture uses a verified media SHA-256 independent of drawing ordinal. Ico starts at logical paragraph offset zero; numeric bookmark IDs are not expectations. TOC field is TOC \o "1-1" \h \z \u, with generated About MOS 2019 Course/Author/Contact entries and no page-number grading. Replacement count is one and retains the exact email/surrounding body paragraphs. Two original note texts/anchors become two referenced endnotes; reserved separator/continuation entries are excluded.

## Template output contract and cleanup

P06 T08 and P07 T08 use FileExistsInCustomOfficeTemplates plus expectedFileName Cert.dotx/Notes.dotx. TemplateOutputLocation resolves Environment.SpecialFolder.MyDocuments + Custom Office Templates and validates one exact .dotx basename. File existence is the complete authorized grading condition; no path provenance, identity, content-type or package checks apply. The previous SavedWordTemplate route is retained for compatibility but no production task uses it. File-existence grading does not save, create, open or alter the template. Other tasks save and inspect the live OOXML snapshot as before.

TemplateOutputCleanupService registers exact task-declared paths for opened/selected Training projects and retains them after switching. MainForm safely saves/checkpoints/closes and disposes the owned Word instance before Cleanup. Only exact declared files are deleted; the folder is never recursively cleaned. Reparse-point targets are refused, errors logged, unrelated templates preserved. Restart/Grade do not clean outputs mid-session. This contract deliberately removes declared Cert/Notes names on normal Training shutdown; no provenance test protects an older same-named file, matching the latest authorized simplified workflow.

Real Word-created answers, 56 package matrix cases, eight template-existence cases, real-form EN/VI integration, production VS F5, ownership sentinel test, P01–P05 combined regression, starter/reference hashes and final build logs are retained under ignored artifacts/phase11-p07. P06 and P07 combined answers each pass 8/8 while their declared template files exist; after normal shutdown T08 correctly fails until the file exists again. Historical phase10 evidence uses the older, superseded T02/T08 acceptance contract.

Solution: MosWord2019.slnx, supported by local Visual Studio Community 18 / MSBuild 18.10.1. All five projects use classic MSBuild format, C# 7.3, .NET Framework 4.7.2, AnyCPU. WinForms outputs MosWord2019.exe. Use Visual Studio MSBuild, not an assumed dotnet build workflow:

Core and Projects explicitly list their source files. New source files in either project must be added to its Compile items; IWordWindowLayout.cs is included exactly once under Interfaces. Reload the project in Visual Studio after external project-file changes. This avoids stale IDE source-item evaluation hiding the interface while command-line wildcard builds succeed.

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

Go validates selected translations, saves/closes existing work, prepares through TrainingWorkspaceService, opens only the returned .docx or legacy .doc working copy and selects Task 1. Navigation only updates the instruction panel. For OOXML tasks, Grade saves the live document, checkpoints supported Save As output to work.docx, snapshots the live FullName, and grades the selected tab without closing, reopening, resetting, or moving Word. Save/copy failures route to a technical Error message and keep Word open. Confirmed Restart (default No) closes without saving, resets via the workspace service, reopens and selects Task 1. Manual document/application closure is detected through IsOpened; stale state is cleaned, a localized message is shown and Go can reopen saved work. Form close saves, closes and disposes on the same STA thread. Phase 2 ownership protection remains authoritative. Testing controls are absent.

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

Word owns its .git, main branch, and dedicated origin. P01-P07 implementation and supplied P08 resources are committed at the starting HEAD. P08 integration/grading is local and uncommitted. The actual Excel Git root is the parent, not ../MosTrainer; never run a Git mutation in the parent. Parent status naturally lists MosWord2019/ as untracked. Do not hide that by modifying parent ignore rules. Reference/hash manifests are retained in ignored artifacts/.

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

The authoritative production source root is `MosWord2019.WinForms/Projects`. The classic WinForms project explicitly includes each supplied project file once with PreserveNewest, without Link metadata or duplicate build items, producing AppDomain.CurrentDomain.BaseDirectory/Projects at runtime. MainForm passes this root to ProjectLoader and filters to .docx. P01 through P05 each have eight supplied tasks and display deterministically as Project 1 through Project 5. Invalid packages are omitted; with none available, Go and task actions are disabled.

`ProjectMeta` contains ProjectId, OfficeVersion, Version and a Word default of `starter.docx`. `ProjectPackage` aggregates metadata, tasks, the selected language dictionary and an absolute package folder. DisplayName recognizes `Word2019_Pnn` and returns `Project n`. No Excel prefixes or workbook fields exist.

`TaskDefinition` contains only ProjectId, TaskId, TitleKey, InstructionKey and AssertionType. ProjectId is assigned from validated metadata and ignored during JSON serialization. JsonExtensionData stores arbitrary future assertion parameters in a case-insensitive `IDictionary<string, JToken>`. AssertionType must be nonempty for a task, but the validator does not call WordGradingService or maintain a fake support list.

ProjectLoader enumerates only top-level directory names matching case-sensitive `Word2019_P` plus at least two digits. It validates each directory, omits invalid packages, selects the exact requested EN or VI dictionary, resolves an absolute folder path and orders results by ProjectId with an ordinal case-insensitive comparer. There is no language fallback; a missing requested file is an error. GetText returns a selected translation or the key when absent.

ProjectValidator performs structural checks without Word: package directory; readable meta/tasks JSON; nonempty convention-compliant projectId exactly matching its folder; explicitly declared root-level starter; approved `.docx` or `.docm` extension; starter existence; nonempty task list; nonempty unique case-insensitive task IDs; title/instruction/assertion fields; lang directory; exact requested EN/VI file; and every title/instruction key in every present language file. It returns stable issue codes in ValidationResult. It deliberately does not inspect OOXML content or validate assertion support.

`TrainingWorkspaceService` owns file operations and never calls Word. Its production root is exactly `%USERPROFILE%\Documents\MosWord2019\Working`; its injectable Documents root supports isolated asset verification. It validates package/project/starter paths, derives `work` plus the lowercase approved starter extension, and confines paths to the package and working roots. PrepareWorkingCopy creates the directory and copies only when work is absent. A second call preserves learner work. ResetWorkingCopy copies the starter to a same-directory temporary file and atomically replaces/moves the closed working file; the caller must close Word first. `StageProjectAssetsToDocuments` stages regular top-level files from the package `assets` directory, preserves an identical destination, and rejects a same-name different user file without overwriting it. MainForm stages before open and restart. Copied files have only their read-only attribute removed. The starter is never written.

`.docx` and `.docm` are approved package/workspace extensions so extension handling does not require redesign. Phase 2 WordController currently accepts only `.docx`; `.docm` activation and macro policy remain explicitly deferred.

## Phase 3 verification handoff

The ignored disposable `Word2019_P99` fixture was created outside the production root, including a valid starter generated by Word Interop, one clearly nonproduction task with arbitrary deferred assertion text, EN/VI dictionaries and an empty assets directory. It was never returned by the production runtime root and was deleted with the harness after verification.

`artifacts/phase3-runtime.log` records passes for valid EN/VI packages, extension-data retention, missing/invalid meta, missing starter/tasks, duplicate task ID, missing requested EN/VI, missing language key, missing assertionType, invalid project ID, Word-only discovery, deterministic ordering, display names, selected language, resolved folder, first-copy creation, second-copy preservation, reset, unchanged starter SHA-256, exact default Training root, and Phase 2 open/save/close/process cleanup. Initial and final WINWORD sets were empty. Debug and Release logs show zero errors and warnings. Both outputs contain the copied production-root README and Newtonsoft.Json assembly.

At the Phase 3 checkpoint no production content or grading existed. Phase 4 added Training orchestration and Phase 4B integrated the supplied P01 without inventing instructions. Package/workspace services and lifecycle cleanup remain stable; later sections describe the P01–P05 grader. Testing, score, timeout, database and installer remain unimplemented.

## Phase 4B layout and P01 contract

MainForm uses Segoe UI, WhiteSmoke, normal title bar and AutoScaleMode.Font. The top selector keeps a bounded width. Each tab uses the supplied TitleKey translation and a noneditable wrapping instruction label with scrolling on small displays. Clicking tabs and << / >> only affects UI state. Grade is absent. Normal close and switching save before closing; Restart defaults to No and explicitly discards only after Yes.

Screen.FromControl(this).WorkingArea is divided into the lower quarter for the trainer and the remainder for Word. A font-relative minimum height protects readability. Load, display/work-area notifications, font/DPI notifications and ResizeEnd recalculate placement. SystemEvents handlers are unsubscribed during disposal. The single TableLayoutPanel column uses 100% width so font scaling cannot expand it offscreen.

Core IWordWindowLayout is an optional placement contract separate from IWordController. WordController.SetWindowBounds obtains a short-lived Window from its held Document.ActiveWindow. WinApiProcessHelper.OwnedProcess.PositionWindow checks that its retained process handle is still alive and the HWND PID matches before restore/SetWindowPos (no Z-order/focus change). It never searches globally for an arbitrary Word window. The Window RCW is released in finally; no lifecycle ownership/cleanup rules were weakened.

P01 assertion identifiers are DocumentStyleSet, BulletedList, Footnote, HeaderDifferentFirstPage, SymbolInserted, PictureArtisticEffect, TableCellsMerged and PictureWrapType. Phase 5 implements all eight against generic TaskDefinition.Extra metadata.

## Phase 5 grading architecture

`WordGradingService` is a pure Core service. It copies a saved Word package into memory through a read-only FileStream with FileShare.ReadWrite/Delete, releases the source handle, and grades OOXML parts from the snapshot. It never creates or attaches to Word. `TaskGradeOutcome` separates Pass, Fail and Error so corrupt packages, missing metadata and unsupported assertions cannot appear as learner failures. `IsAssertionTypeSupported` recognizes 53 implemented identifiers; production P06 DocumentPageBorder has confirmed color metadata.

## Phase 6 P02 grading architecture

P02 adds generic text removal/replacement, text-to-table, automatic TOC, text-box content, targeted comment deletion, exact paragraph spacing and whole-paragraph character-style assertions. They route by AssertionType and TaskDefinition.Extra metadata, never by P02 task ID. Ground truth from Word 2019 is retained under ignored `artifacts/phase6-p02` and `artifacts/phase6-bugfix`: T03 produces a 5-row, 3-column table. The grader checks the instructed columns, rows, cell content and order without requiring incidental table width/layout serialization. T04 is a Table-of-Contents SDT with `TOC \\o "1-3" \\h \\z \\u`; T05 uses the DrawingML Choice shape at CONTACT US with rect geometry and fill 002060 and permits automatic uppercase only through explicit metadata; T06 removes only the comment range on `proud`; T07 serializes 14 pt as line 280/exact; T08 applies the IntenseEmphasis character style to every visible run while tolerating run splitting and comment-reference runs without visible text.

DocumentStyleSet hashes a curated, normalized semantic set of document styles and ignores change records, rsids, relationship IDs and other volatile IDs. HeaderDifferentFirstPage requires current `w:titlePg`, no first-page header reference, and a normalized Integral primary-header structural signature. BulletedList resolves numId to abstractNum/level and checks the exact consecutive paragraph block plus effective 0/360-twip geometry. Footnote, SymbolInserted and TableCellsMerged require exact anchors/content/structure. Picture assertions resolve drawing relationships to media bytes and identify the target by SHA-256 rather than shape order. Pencil Sketch is `a14:artisticPencilSketch`; Square is `wp:anchor/wp:wrapSquare`.

Word 2019 ground-truth serialization came from disposable copies of the production starter. Lines (Stylish), Integral, Webdings 126 (`w:sym`/`F07E`), Pencil Sketch and Square were observed rather than inferred. DocumentStyleSet checks selected stable style-property paths and attributes from repeated apply/save/reopen samples, not a full style XML hash. HeaderDifferentFirstPage requires `titlePg`, an absent or semantically empty first-page header and the stable Integral table/fill/title-control structure in the primary header. Applying Pencil Sketch rewrites the source image to a deterministic PNG and adds an HD-photo layer, so T06 accepts the starter source fingerprint and the verified transformed fingerprint before checking the effect.

The P01 starter at Phase 5 blob `e7f2f69c12d7bf12464fc43c70b37916a052c40f` had SHA-256 `3D906B8A4C7E2DA3272DEC63A30E38CB5354651290A29B8E199BFAA223F83FED`. The current blob `910b3c2dd2bdaef958e76345707c67ae60ab873e` has SHA-256 `0A86091C0B58E39D7CDCC9D7F19919AA7182467794927FAEB11F620D75D151E4`; its meaningful task-state change is the T08 target wrap from Square to Tight. `styles.xml` is identical between the two blobs, so this history is unrelated to the T01 style-set false negative. Keep the current starter; never revert it as a grading workaround.

Ignored `artifacts/phase5` evidence covers fresh, correct, incorrect and near-miss documents, combined 8/8 grading, English/Vietnamese UI results, Save-error routing, unchanged Word PID during grade, layout, starter integrity and process cleanup. No artifact is part of the production project tree.

## Phase 7 P03 grading architecture

P03 uses eight generic assertions routed by AssertionType and TaskDefinition.Extra: TableAccessibilityFirstRow, SectionOrientationByAnchor, TableColumnWidthsEqual, CitationPlaceholderAtParagraphEnd, SmartArtDirectionEquals, SmartArtAltTextDescriptionEquals, CorePropertyEquals and ParagraphFormattingMatches. Target selection uses semantic table rows, section content anchors, diagram relationships, exact core-property names and exact paragraph text. No assertion routes by ProjectId or TaskId.

Word 2019 ground truth shows: P03 T01 Accessibility `Use first row as header` sets `w:tblLook` first-row semantics on the target table and is distinct from the `w:tblHeader` Repeat Header Rows command. T02 changes only the existing section containing `MOS Version` to landscape. T03 writes grid widths near 2261 twips for 1.57 inches without explicit row heights. T04 creates a citation SDT at the paragraph end with field `CITATION MOS \\l 1033` and a placeholder-only bibliography Source. P03 T05 Right-to-Left is the diagram-data state without `dgm:dir val="rev"`; the starter's `rev` state is opposite. T06 writes `wp:docPr descr="Process flow"`. T07 is `docProps/core.xml` `cp:category`. T08 applies `IntenseEmphasis` to all visible paragraph runs plus justified paragraph/paragraph-mark formatting. Citation runs are excluded from T08's visible-body formatting check.

The supplied P03 starter is in the opposite T05 SmartArt direction, so fresh T05 grades Fail. The user-supplied answer and fresh Word ground truth grade Pass; changed SmartArt data remains a failure.

P02 T05 now treats one `mc:AlternateContent` as one logical shape: it prefers readable DrawingML Choice and uses the VML fallback only when Choice has no usable textbox. Text is concatenated across every `w:t`; exact mixed-case, mixed-case rendered with `w:caps`, and explicit uppercase are accepted only under the task's `allowAutomaticUppercase` metadata. Lowercase without caps, punctuation, extra text, wrong fill/geometry/anchor, body text and another textbox fail. The original reported failure used a stale pre-fix Debug binary; final production F5 loaded the rebuilt Core and returned Correct/Đúng before and after reopen/resave.

Ignored evidence under `artifacts/phase7-p03` contains Word-created ground truth, negative/near-miss variants, 8/8 combined grading, package/workspace checks, production F5 EN/VI results, switching, restart, layout and lifecycle logs. Ignored `artifacts/p02-t05-real` contains the exact P02 split-run/caps diagnostic and production F5 regression. Production starters were not modified.

## Phase 8 P04 grading architecture

P04 adds TableRowsEqual, ListLevelEquals, InlineModel3D, ContinuousSectionBreakBeforeHeading and SmartArtAllNodesBevelEquals, and it reuses PictureArtisticEffect, TableFirstRowIsHeader and PictureWrapType. TableAccessibilityFirstRow was added for the corrected P03 T01. The total supported set is 30 assertions. All dispatch remains metadata-driven; there are no P03/P04 task-ID branches.

P04 Word 2019 ground truth establishes: T01 exact Date-then-Score row order and unchanged cells; T02 zero-based `w:ilvl=2` within the same real numbering definition; T03 a unique `am3d:model3d` in an `mc:Choice` and `wp:inline`, related to a `.glb` whose SHA-256 is `44741A42017FC0A5DE7641E3D2FC3216AEC751D3576C00F5E1D2AF69FB2FB323`, in the blank paragraph before `Instructors`; T04 Pencil Sketch on the fingerprinted IC3 certificate picture; T05 a local Continuous boundary immediately before `MOS 2019`; T06 `w:tblHeader` on only the first row of the MOS table; T07 `a:bevelT prst="softRound"` on every text-bearing node of the target SmartArt data part; and T08 Square wrapping on the fingerprinted MOS Certification picture.

The supplied P04 starter already satisfies T05, so fresh T05 passes; this immutable-content limitation is explicit. `Project 04-Fix.docx` and a fresh Word answer pass with one or more adjacent blank Continuous boundary paragraphs; Next Page and a Continuous break elsewhere with a non-Continuous closest boundary fail. Baseline/positive/negative/near-miss checks pass for all assertions, P04 combined grades 8/8, and combined P01/P02/corrected-P03 remain 8/8. Production Visual Studio F5 verifies EN/VI grading, all tabs, Restart, project switching, unchanged owned PID during grading, layout, absent Save controls and final process cleanup. Evidence is ignored under `artifacts/phase8-p04` and `artifacts/phase9-p05`.

## Phase 9 P05 grading architecture

P05 adds `DocumentParagraphLineSpacingMultiple`, `ContinuedNumberingSequence`, `ParagraphBlockColumns`, `ParagraphBlockKeepWithNext`, `CommentResolved`, `ShapeWithTextWrapAndPosition`, and `HeadersFootersWatermarksRemovedByState`; T05 reuses `CitationPlaceholderAtParagraphEnd`. All assertions are routed by `AssertionType` and `TaskDefinition.Extra`, never by project/task ID.

Word 2019 ground truth establishes: T01 `w:spacing` line 336/auto on every main-story paragraph; T02 all six exact list paragraphs share one level-0 numId whose decimal definition starts at 1 and has no override; T03 exactly four paragraphs occupy the two-column section with 576-twip spacing and one-column neighbors; T04 the five selected following paragraphs carry direct `keepNext`, while the Heading 1 style supplies it for the heading; T05 is a real `CITATION Signature1 \\l 1033` SDT/field and placeholder bibliography source; T06 targets the starter's sole comment on `founder information`, and Resolve retains the comment/range while setting `commentsExtended.xml` `w15:done="1"`; T07 is DrawingML preset `horizontalScroll`, exact text `Congratulation!`, `wrapSquare`, and page-relative center/bottom placement anchored in the bibliography placeholder paragraph; T08 Document Inspector empties the active header content and adds empty header/footer parts and references for the other sections while body text, comment, bibliography source, core creator, citation and shape remain. It deletes no package part in the observed answer.

P05 baseline, positive, negative and meaningful near-miss cases pass for all eight tasks, and the Word-created combined answer passes 8/8. Combined P01 through P04 remain 8/8. P01–P05 validate with zero EN/VI errors/warnings and load as Project 1 through Project 5. Real WinForms/Word smoke grades all P05 tasks `Correct` and `Đúng`, retains one owned PID during grading and P04↔P05 switching, restarts to Task 1, confirms P04 user-answer T05 `Correct`, and leaves no owned WINWORD process.

P04 assets are staged before open and restart. The verified production destination is `%USERPROFILE%\Documents\Glasses.obj`; an identical file is preserved and a different existing file is rejected. Word 2019 inserted the staged Documents file into a disposable P04 starter copy as the expected inline 3D model, and T03 graded Pass. The staged test copy was removed afterward. P05 starter SHA-256 is `3ACBFD8450BA4EF6A2F619377E2F115437009EBB818D594BF3358DDB9AA32D0B`. Evidence is ignored under `artifacts/phase9-p05`.

Phase 4B evidence is under ignored artifacts/phase4b. Real P01 EN/VI validation, runtime navigation/persistence/restart/save/cleanup, starter hash and unrelated-window rejection passed. Actual desktop 125% working-area bounds: 1920x1020; trainer 1920x255 at y=765; Word 1920x765 at y=0. Equivalent 100/125/150% font-layout tests passed; this is not a claim of testing other OS display settings or multi-monitor hardware. Visual Studio showed README in Solution Explorer and 0 errors/0 warnings. Test working copies/harness binaries/source were removed.

## Phase 4 verification handoff

artifacts/phase4/runtime-final.log records zero failures across Tests A-K, focused lifecycle/package regressions, save-failure cancellation, missing runtime translation failure and .docm exclusion. The harness injected artifacts/phase4/fixtures/packages with Word2019_P98 and P99, Word-created starters, three explicitly non-MOS tasks and EN/VI dictionaries. Working files were also isolated under fixtures. Disposable fixtures and harness binaries/source were removed. Build logs and EN/VI form renders remain ignored. Initial/final Word PID sets were empty; a separate unsaved sentinel was preserved throughout the controller tests. The final corrected harness cleaned its own sentinel successfully. No production starter was edited or opened for learner work during verification.
