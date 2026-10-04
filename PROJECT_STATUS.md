# Project Status

Verified: 2026-10-04. Earlier phase evidence below retains its historical dates.

## Current Project 8 state

Verified 2026-10-04 on main HEAD `f1615f23fe24610f781f0aebb7988a508ed10eb9`; starting status clean. P01-P07 are committed; P08 changes remain local/uncommitted. No commit or push.

P08 has exactly eight tasks. P01-P08 EN/VI source and actual Debug/Release output validate with 0 errors/0 warnings; discovery is Project 1-8. Four new metadata-driven OOXML assertions bring the supported set to 53. Footnote adds optional logical reference location/preceding heading; text-range formatting adds optional inherited paragraph alignment. Existing P01-P07 metadata, UI and Word lifecycle source are unchanged.

Real Word 2019 16.0.14026 saved/reopened positives pass all eight P08 assertions. All 32 baseline/positive/negative/near-miss checks pass, combined P08 is 8/8, 18 supplemental semantic cases pass, and Word-created tracked-format/section-layout Reject passes while Accept fails. Established combined P01-P05 remain 8/8; the real EN/VI form harness verifies P06/P07 8/8 with real Cert/Notes Save As, P07 legacy Convert/Restart, P07-P08 persistence, P08 baseline/combined/Restart, same PID grading and normal template cleanup. The final harness has zero failures. An independent unsaved Word sentinel and the user Word process survive owned cleanup.

Production Visual Studio F5 verifies EN/VI login, P08 work.docx/eight tabs, upper Word/lower trainer layout, no Save controls, native Resolve Comment and insertion/deletion acceptance with Correct, VI Sai/Đúng, and native Header Row correction with Đúng. Owned Word exits on normal form close. **Remaining acceptance item:** the initial disposable native Accessibility Checker reported No header row with Use first row as header first; later production F5 Checker reported No accessibility issues found even with Header Row unchecked. Its cause is not established. The grader was not weakened; recommended-action UI reproduction in production remains a concrete verification item. Therefore full P08 classroom/UI acceptance is partial, although grading/package/regression checks above pass.

The starter contains no formatting/layout revision. Combined ground truth handles supplied text revisions first and stops tracking before other task edits; rejecting new tracked formatting afterward can undo those edits. The generic disposition assertion was separately verified against real Word tracked Bold and section-margin revisions.

Debug/Release Clean/Rebuild: 0 errors, 0 warnings. All eight starter hashes and all 185 tracked Excel-reference file hashes are unchanged. Evidence: ignored artifacts/phase12-p08. Testing Mode remains not started.

## Previous Project 6 corrections and Project 7 acceptance

P06 corrections and P07 acceptance below are historical verification; they are now committed in the current baseline f1615f23fe24610f781f0aebb7988a508ed10eb9. No historical evidence is reclassified as a new test.

P01–P07 each have eight tasks and validate EN/VI with zero errors/warnings. Discovery is Project 1–7. Core routes 49 generic assertion identifiers (including retained SavedWordTemplate compatibility); production P06/P07 T08 now uses FileExistsInCustomOfficeTemplates.

P06 T02 is finalized from the user's confirmation: Red, Accent 1, effective B71E42, themeColor=accent1, four single Box edges sz=24 (3 pt), all pages of every section. A fresh Word 2019 answer and production F5 return Correct. Dark Blue, different red, wrong theme semantic and first-page-only negatives fail. Starter/theme are unchanged.

P06 Cert.dotx and P07 Notes.dotx are graded solely by exact file existence under Documents/Custom Office Templates. Grade neither creates nor opens/alters those outputs. Their exact task-declared names are retained across project switches and cleaned after safe save/close and disposal on normal Training shutdown. F5 verified both outputs existed and graded Correct before exit, both were removed afterward, and an unrelated template retained its hash. Restart does not delete templates during the session.

P07 deliberately starts from immutable starter.doc. Its initial work.doc opens in Compatibility Mode 11. Learner File > Info > Convert changes the live mode to 15; FullName remains .doc until Save, which produces work.docx (SaveFormat 12). Saved/reopened work is modern. Live FullName and workspace checkpointing preserve modern work across switches; Restart restores the binary legacy work.doc, removes the exact owned work.docx checkpoint, and selects Task 1. No automatic conversion occurs. Other OOXML tasks before Convert return a technical message, not a false learner result.

Real Word 2019 Version 16.0 / Build 16.0.14026 produced saved/reopened P07 answers. Target header is MO-100: MICROSOFT WORD (OFFICE 2019), not the CONFIDENTIAL cover text; its blue/shadow preset uses effective 4472C4 and w14:shadow. Author image is located by its verified media fingerprint, Ico by paragraph offset zero, TOC by real field range 1-1, email replacement by unchanged main-body text, and endnotes by preserved content/anchors. Two user footnotes become zero user footnote references and two user endnotes; reserved separators are excluded.

Combined P07 and corrected P06 each pass 8/8 in EN and VI, including actual Word Save As outputs. The 56-case package matrix and eight template-existence cases pass; baseline, negatives, near-misses and split-run/volatile-ID equivalents are covered. P01–P05 established Word-created combined answers remain 8/8 each. Real-form tests cover conversion, persistence, Restart No/Yes, Task 1, manual closure recovery, same PID grading/switching and cleanup. An independent unsaved Word sentinel remained alive and unchanged while Trainer graded and closed; owned automation security remained ForceDisable (3). Final Word process set is empty.

Production Visual Studio F5 verified Training EN/VI, Project 1–7 discovery, eight tabs, legacy opening, learner Convert, Correct T01/T02/T05/T06/T07/T08 and VI Đúng/Sai, P06 T02 Correct, actual Cert/Notes Save As, cleanup, and Word upper/trainer lower layout without Save buttons. The pre-existing user P06 working copy was preserved and was not restarted or replaced by a fixture. Ignored evidence is under artifacts/phase11-p07.

Final Debug/Release Clean/Rebuild each report 0 errors and 0 warnings. Both actual output trees validate P01–P07 EN/VI 0/0 and include the legacy P07 starter.doc. git diff --check passes. All seven production starter hashes and all 185 tracked Excel-reference file hashes remain unchanged. Testing Mode remains not started.

Build repair: Core now explicitly lists its ten source files, including exactly one Interfaces/IWordWindowLayout.cs entry. Namespace/import/Word-to-Core ProjectReference were already correct. Clean, standalone Core and Word builds, and full Debug/Release rebuilds each completed with zero errors/warnings. Visual Studio reloaded Core and successfully launched WinForms with F5; Login -> Training -> English -> Project 1 -> Go opened work.docx with eight tabs and upper/lower window placement. No UI, lifecycle or P01 source changes were made by this repair. Logs: artifacts/phase4b-repair.

Projects build repair: TemplateOutputCleanupService already existed with the correct namespace/import/reference, but Visual Studio retained a stale source-item evaluation. Projects now explicitly lists all six source files instead of the wildcard. Reloading Projects and launching WinForms with F5 removed CS0246; the IDE shows 0 errors/0 warnings. Standalone Projects Debug and full Debug/Release Clean/Rebuild each report 0 errors/0 warnings. Logs/screenshots: artifacts/template-cleanup-build-repair. Cleanup/grading/lifecycle behavior is unchanged.

## Phase state

- Phase 0: complete. Independent Word repository, main branch, dedicated origin, protected Excel reference.
- Phase 1: complete. Five classic C# .NET Framework 4.7.2 projects in MosWord2019.slnx and a minimal WinForms shell.
- Phase 2: complete. Real Word lifecycle, explicit saving/discard, deliberate COM release, process ownership and deterministic disposal implemented and verified.
- Phase 3: complete. Word package models, structural loading/validation, build-copy contract, and Training working-copy service are implemented and verified without production task content.
- Phase 4 including 4B: complete. Real P01 (eight tasks), bilingual Training entry, bottom-quarter task tabs, owned Word upper-area placement, internal save/close, switching, confirmed restart and manual closure recovery are verified. No Save buttons appear in the task panel.
- Phase 5 — Project 1 grading: complete and committed at `64c34c5f6468383e46c6262f824ab14b91188aaa`.
- Phase 6 — Project 2 integration/grading: complete and committed through focused correction `a969f5c592ab92b3e551c68a8242f114dd283f75`. P02 validates in EN/VI, appears as Project 2, and grades all eight tasks from saved OOXML. Testing Mode, scoring, database, installer, and release work remain unimplemented.
- Phase 7 — Project 3 integration/grading: complete in the current committed baseline, including the corrected Accessibility-header and SmartArt Right-to-Left semantics supplied by the user. P03 validates in EN/VI and its combined answer grades 8/8.
- Phase 8 — Project 4 integration/grading: complete in the current committed baseline. The committed P04 follow-up stages `Glasses.obj` safely to Documents and fixes T05 to grade the local Continuous boundary instead of a global section count.
- Phase 9 — Project 5 integration/grading: complete and committed in b703bf5/current baseline. P05 validates in EN/VI, appears as Project 5, and grades all eight tasks from saved OOXML. Combined P01 through P05 answers each grade 8/8. Testing Mode, scoring, database, installer, and release work remain unimplemented.
- Project 6 integration/grading: complete; the earlier implementation is committed and the authorized T02/T08 corrections are committed in the current baseline.
- Project 7 integration/grading: complete and committed; real legacy input, eight tasks and combined EN/VI 8/8 verified.

## Current architecture

Core owns Word-specific package/validation models and lifecycle/layout contracts. Projects owns deterministic Word2019_Pnn discovery, structural validation, language loading, Training working copies, safe asset staging and Save As checkpointing. The sole production source root is MosWord2019.WinForms/Projects; classic MSBuild copies its content to the runtime Projects directory without duplicate items. P01-P08 validate in EN/VI and appear deterministically as Project 1-8 with eight tasks each. Starter files are unchanged.

IWordController defines IsOpened, StartWord, OpenDocument, Save, CloseDocument, Close and IDisposable. WordController owns one visible Word application and at most one .docx or legacy .doc working copy. WordSession stores only owned state; WinApiProcessHelper captures and retains the verified process handle. Core WordGradingService snapshots the saved package with FileShare.ReadWrite and evaluates normalized OOXML without Office COM. MainForm saves and grades only the selected task, keeps Word open, and presents bilingual Pass/Fail/Error results. Login permits Training only and stores en/vi in AppSession.

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

Phase 4B is committed at `cbabacab4644df07f6b13247e8546bed8bf0f49c`. P01 grading is committed at `64c34c5f6468383e46c6262f824ab14b91188aaa`; Phase 6 P02 is committed through focused correction `a969f5c592ab92b3e551c68a8242f114dd283f75`. Current main HEAD is `f1615f23fe24610f781f0aebb7988a508ed10eb9`, containing committed P01-P07 work and supplied P08 resources. Current P08 implementation is local/uncommitted; no commit or push was performed.

Excel's actual Git root is the parent; WinForms lives under ../MosTrainer. Reference HEAD: a75a89fc8a5502364e9b3b8b4eb9bc003d23a29d. Starting parent status: pre-existing untracked Installer/Output/MOS_Excel_2019_Setup_v1.0.0.rar and the separate MosWord2019/ folder. Final verification: all 185 tracked Excel files and the existing installer archive retained their hashes; parent HEAD, tracked diff and status are unchanged. No Excel file or parent Git configuration was edited. Do not edit parent Git settings or ignore rules.

## Phase 5 verification

Supported assertions: DocumentStyleSet, BulletedList, Footnote, HeaderDifferentFirstPage, SymbolInserted, PictureArtisticEffect, TableCellsMerged, and PictureWrapType. Word 2019 ground-truth copies establish semantic properties for Lines (Stylish) and Integral, Webdings F07E serialization, `a14:artisticPencilSketch`, exact list/footnote/table structures, and target-image Square wrapping. Each assertion passed positive, negative, and meaningful near-miss checks; one combined answer passed 8/8.

The real WinForms/Word STA smoke passed English fresh T01 Incorrect, English combined T01/T03/T07/T08 Correct, Vietnamese T01 Đúng, current-task selection, safe Save-before-grade, technical Save-error routing, unchanged Word PID during each grade, upper/lower window placement, and owned-process cleanup. Initial and final unrelated WINWORD IDs were identical. Evidence is under ignored `artifacts/phase5`.

The Phase 5 starter SHA-256 was `3D906B8A4C7E2DA3272DEC63A30E38CB5354651290A29B8E199BFAA223F83FED`. The later supplied starter committed in `e57d22d` has SHA-256 `0A86091C0B58E39D7CDCC9D7F19919AA7182467794927FAEB11F620D75D151E4` and changes the T08 target from Square to Tight, so the current fresh T08 correctly grades Fail. An OOXML audit found this task-state change plus view/revision metadata; `styles.xml` is unchanged. The current starter remains authoritative and was not reverted.

## Next recommended task

Integrate the next user-supplied Word project through the same Word-ground-truth and regression process. Testing Mode remains not started.

## Phase 8 Project 4 verification

P03 T01 now distinguishes the Accessibility Checker action from Repeat Header Rows: the supplied Word 2019 answer sets the first-row semantics in `w:tblLook` and does not require `w:tblHeader`. P03 T05 now follows the supplied Right-to-Left answer: Word 2019 serializes the requested state without `dgm:dir val="rev"`; the starter's `rev` state is the opposite. The supplied answer passes T01/T05, starter and semantic near misses fail, and the corrected P03 combined document passes 8/8.

This work adds six reusable assertion types: TableAccessibilityFirstRow for corrected P03 T01, plus TableRowsEqual, ListLevelEquals, InlineModel3D, ContinuousSectionBreakBeforeHeading and SmartArtAllNodesBevelEquals for P04. P04 also reuses PictureArtisticEffect, TableFirstRowIsHeader and PictureWrapType. The total supported assertion set is 30. Real Word 2019 ground truth confirms the exact two-key table order, real list level and resolved numbering definition, `am3d:model3d` inline GLB relationship/hash and location, Pencil Sketch, Continuous section boundary, repeating `w:tblHeader`, Soft Round on every SmartArt content node, and Square wrapping on the fingerprinted picture.

All P04 assertion baseline/positive/negative/near-miss checks pass. The supplied P04 starter already contains the requested Continuous section break before `MOS 2019`, so fresh T05 correctly passes. The user-supplied `Project 04-Fix.docx` and a fresh Word 2019 answer both pass after changing the assertion to the closest local boundary; adjacent blank Continuous boundary paragraphs are accepted. Next Page and a Continuous break elsewhere while the closest boundary is not Continuous fail. P04 combined passes 8/8. Combined P01, P02 and corrected P03 remain 8/8, with focused negative regressions retained.

P01–P04 EN/VI validation reports zero errors and warnings; discovery order is Project 1, Project 2, Project 3, Project 4. P04 first-copy, preserve, reset and immutable starter checks pass. Production Visual Studio F5 shows all eight P04 tabs, grades all eight English tasks `Correct` and representative Vietnamese tasks `Đúng`, keeps the same owned Word PID while grading and switching, restores the starter on Restart, preserves upper/lower placement, and has no Save or Save/Close buttons. The initial and final WINWORD PID sets match.

Final starter SHA-256 values are P01 `0A86091C0B58E39D7CDCC9D7F19919AA7182467794927FAEB11F620D75D151E4`, P02 `63A40EAD1EAB7BD4AAF5ECA672B36C784D07A7E3A912C0F4D2C8BB862308F23C`, P03 `8491694AE5872FAA5D82D045D5D173DB3656C2652BE4C0E6CB6CC692B032AE05`, and P04 `696015203B45BAD814B60F45047A7F51B0B4B25580246E8E26F2AB32BC5BE95E`. The supplied/production Glasses.obj hash is `6FAE672E7A49AB67A0F091F94FEFE8780EB5049B0FE8FF7C7AFD2A0804E919D8`. Debug and Release rebuilds both report zero errors and warnings. Evidence remains ignored under `artifacts/phase8-p04`.

## Phase 9 Project 5 verification

P05 adds seven generic assertions and reuses `CitationPlaceholderAtParagraphEnd`: document-wide Multiple line spacing, a real continued numbering sequence, an exact paragraph block in a two-column section, Keep with next across a verified paragraph block, resolved-comment state, a shape with exact text/wrap/page-relative placement, and Inspector-clean headers/footers/watermarks while protected categories remain. The total supported assertion set is 37. All routing remains metadata-driven.

Word 2019 ground truth confirms T01 `w:line=336` with `lineRule=auto`; T02 one `numId` and no level override for the 1–6 sequence; T03 exactly four paragraphs between Continuous boundaries with two columns and 576-twip spacing; T04 direct `keepNext` on the five following paragraphs while the Heading 1 style supplies it for the heading; T05 the real `CITATION Signature1 \\l 1033` placeholder; T06 the only supplied comment is on `founder information` and Resolve changes `commentsExtended.xml` `w15:done` from 0 to 1 while preserving the comment; T07 a `horizontalScroll` DrawingML shape with Square wrap and page center/bottom alignment; and T08 leaves no visible content in active headers/footers/watermarks while preserving the resolved comment, bibliography source, core creator, body text and inserted shape.

Every P05 baseline fails, every Word-created positive passes, and the focused negative/near-miss matrix rejects partial/wrong spacing, separate or typed numbering, wrong columns/spacing, partial Keep with next/Keep lines together, typed or wrong-case citation, deleted comment, wrong shape/position, and removal of the comment category. A Word-created combined answer passes 8/8. P01–P04 regression remains 8/8. P01–P05 EN/VI validation reports zero errors/warnings and deterministic discovery shows Project 1 through Project 5.

The real WinForms/Word smoke passes Login EN/VI, P05 eight tabs, all eight `Correct`/`Đúng` results, Restart returning to Task 1, P04 `Project 04-Fix.docx` T05 `Correct`, P04↔P05 switching with the same owned Word PID, upper/lower window placement, absent Save controls, and final owned-process cleanup. P04 opening staged the exact package `Glasses.obj` at `%USERPROFILE%\Documents\Glasses.obj`; Word 2019 inserted that staged file into a disposable starter copy as an inline 3D model and P04 T03 graded Pass. The test-created staged copy was removed afterward. Starter hashes P01–P05 remain unchanged; P05 is `3ACBFD8450BA4EF6A2F619377E2F115437009EBB818D594BF3358DDB9AA32D0B`. Evidence remains ignored under `artifacts/phase9-p05`.

## Phase 7 verification

P02 T05 was reproduced with the user's exact Word representation: one DrawingML Choice target plus its VML fallback, two `w:t` values (`Anytime Account Acces` + `s`), and effective `w:caps`. Production `TextBoxTextEquals` now reads Choice first and falls back to VML only when Choice is unavailable, so the same AlternateContent is not double-counted. The original UI failures occurred before commit `a969f5c` and used a stale Debug Core binary; the final Debug executable loads the rebuilt Core DLL. Production F5 returns `Correct` on first open and reopen/resave and `Đúng` in Vietnamese. Exact-text false-positive variants continue to fail.

P03 uses TableAccessibilityFirstRow, SectionOrientationByAnchor, TableColumnWidthsEqual, CitationPlaceholderAtParagraphEnd, SmartArtDirectionEquals, SmartArtAltTextDescriptionEquals, CorePropertyEquals and ParagraphFormattingMatches. The user-supplied answer established the corrected T01/T05 semantics; disposable baseline/positive/negative/near-miss documents cover them without changing the other six assertions. The corrected combined P03 answer passes 8/8, while combined P01 and P02 remain 8/8.

P03 EN/VI validation reports zero errors and warnings. First-copy, preserve, reset and immutable starter checks pass. Historical Phase 7 UI/lifecycle evidence remains valid; the corrected T01/T05 grading was additionally verified through the supplied answer, focused matrices and the corrected 8/8 combined document.

Final starter SHA-256 values are P01 `0A86091C0B58E39D7CDCC9D7F19919AA7182467794927FAEB11F620D75D151E4`, P02 `63A40EAD1EAB7BD4AAF5ECA672B36C784D07A7E3A912C0F4D2C8BB862308F23C`, and P03 `8491694AE5872FAA5D82D045D5D173DB3656C2652BE4C0E6CB6CC692B032AE05`. Final Debug and Release rebuilds report zero errors and warnings. Evidence remains ignored under `artifacts/p02-t05-real` and `artifacts/phase7-p03`.

## Phase 6 verification

P02 `tasks.json` declares eight generic assertion types: TextRemovedFromParagraph, TextReplaceAll, TextConvertedToTable, AutomaticTableOfContents, TextBoxTextEquals, CommentDeletedAtText, ParagraphLineSpacingExact and CharacterStyleAppliedToParagraph. Focused Word 2019 ground truth corrects T03 to the instructed 5-row, 3-column table and confirms T05 stores the entered text under automatic caps formatting. T01 Lines grading now checks selected semantic style properties instead of a full canonical XML hash. T04 accepts an absent or semantically empty first-page header while requiring the semantic Integral primary-header structure. T05 accepts the exact mixed-case text or its all-uppercase representation only when `allowAutomaticUppercase` is enabled.

The focused correction matrix passes: P01 T01 repeated apply/save/reopen/unrelated-task positives and starter/other-set/manual-heading negatives; P01 T04 correct, empty-first-header and reopen positives plus no-different-first/custom/visible-first-header negatives; P02 T03 correct/reopen 5x3 positives plus 2-column, 4-column, missing-cell, wrong-order and plain-text negatives; and P02 T05 mixed-case/all-uppercase positives plus lowercase, wrong-case, punctuation, missing-word, wrong-textbox and body-text negatives. Combined P01 and P02 answers remain 8/8.

Visual Studio `/RunExit` launched the production WinForms startup project. Login -> Training -> English -> Project 1 -> P01 T01/T04 -> Project 2 -> P02 T03/T05 displayed `Correct` for all requested real-Word states. Each grade kept the same owned WINWORD PID; project switching reused it; normal exit removed it; the unrelated PID set was unchanged. Existing Documents/MosWord2019 work copies were backed up and restored byte-for-byte after the smoke run. Final Debug and Release rebuilds report 0 errors and 0 warnings. Production starter hashes remain P01 `0A86091C0B58E39D7CDCC9D7F19919AA7182467794927FAEB11F620D75D151E4` and P02 `63A40EAD1EAB7BD4AAF5ECA672B36C784D07A7E3A912C0F4D2C8BB862308F23C`.

P01/P02 EN/VI validation reports zero errors and warnings; discovery order is Project 1 then Project 2. Every P02 assertion passes its positive document and rejects baseline, negative and meaningful near-miss variants. Combined P02 and P01 answers each pass 8/8. P02 first-copy, preservation, reset and immutable starter SHA-256 checks pass. The real WinForms/Word smoke verifies EN/VI results, representative T01/T03/T04/T06/T08 fresh/correct states, restart, P01-to-P02 switching, same PID during grading, upper/lower placement and owned-process cleanup. Evidence remains ignored under `artifacts/phase6-p02`.

## Current Phase 4B acceptance

- P01 EN/VI structural validation and discovery pass with eight implemented assertion identifiers.
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
