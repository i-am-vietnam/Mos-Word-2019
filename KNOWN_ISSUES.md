# Known Issues and Verification Items

Reviewed: 2026-09-27.

No unresolved defect was observed in the final Phase 2 lifecycle matrix on the local Office 2019 x64 installation.

## Current constraints

- Calls are synchronous and must run on the same STA thread that created WordController. There is no COM-call watchdog, crash-resume, or background automation worker.
- CloseDocument and Close discard unsaved changes. Call Save explicitly first. Save failure leaves the live document available rather than closing it automatically.
- A reproduced backing-file read-only failure caused Word to retain read-only document state after the filesystem attribute was restored. Automatic save retry is not guaranteed; recover needed content through Word (for example Save As) before explicitly closing/discarding it.
- If another document is opened in the owned Word application, Close preserves that application and reports the reason. The same conservative preservation applies when remaining document ownership cannot be inspected. It relinquishes the process handle; the user must close the remaining Word documents. This intentional protection is not an orphan-cleanup success claim.
- OpenDocument supports existing writable, unencrypted .docx working copies only. TrainingWorkspaceService can derive `.docx` or `.docm` working names from approved package starters, but Phase 2 does not yet open `.docm`; enabling macro documents requires an explicit lifecycle/security decision.
- PID capture requires a visible Word OpusApp window on an interactive Windows desktop. The registered Word 16.0 library 8.7 and Office Core 2.8 PIAs are build prerequisites; no hard-coded PIA filesystem path is committed.
- P01 through P05 starters and EN/VI instructions are supplied and structurally valid. All 37 P01–P05 assertion types are implemented. Structural package validation remains separate from runtime supported-assertion dispatch.
- The current supplied P01 starter serializes the Contact Us target picture with Tight wrapping, so fresh T08 grades Fail as intended. An older Phase 5 blob used Square; the later starter revision is authoritative and must not be reverted. The target-specific assertion still rejects Tight/Inline and does not accept a different Square picture.
- Package validation checks declared files, JSON, IDs, languages and task keys without opening Word. It does not yet inspect the starter as an OOXML ZIP package; the disposable starter was independently created and opened with Word.
- ProjectLoader omits invalid packages from its returned list. Call ProjectValidator directly when the UI needs detailed package diagnostics.
- The Training workspace service has no awareness of open COM documents. MainForm closes the active document before ResetWorkingCopy.
- Training runtime exposes only .docx packages; .docm packages are filtered out pending lifecycle verification.
- P01 tasks.json now links all eight supplied task titles/instructions. The starter and EN/VI instruction meaning are unchanged.
- Normal save failures cancel project switching or form closure to preserve live learner content. The learner may need to recover content through Word before retrying. Unexpected cleanup failures are logged and reported; conservative Phase 2 extra-document protection still applies.
- Training grading exists for P01 through P05. Authentication, Testing Mode, scoring, database, and installer do not exist. Login is mode/language selection, not authentication.
- P03 Right-to-Left ground truth shows that the requested state omits `dgm:dir val="rev"`; the supplied starter's `rev` state is the opposite and correctly fails fresh T05. P03 Accessibility header semantics are `w:tblLook` first-row flags, distinct from Repeat Header Rows (`w:tblHeader`).
- The supplied P04 starter already contains the requested Continuous section break immediately before `MOS 2019`, so fresh P04 T05 grades Pass. This confirmed immutable-content limitation was not changed to manufacture a failing baseline. The local assertion accepts adjacent blank Continuous boundary paragraphs created by Word, while Next Page and a Continuous break elsewhere with a non-Continuous closest boundary fail.
- P04 asset staging deliberately refuses to overwrite a different same-name file in Documents. The learner must move or rename that personal file before opening/restarting the project; an identical `Glasses.obj` is preserved.
- P05 T06 wording refers to the `Evaluate and monitor the program` section, but the supplied starter's only resolvable comment is anchored to the `founder information` heading and contains `Beginning of the second page`. Grading targets that actual supplied comment and records Resolve through `commentsExtended.xml` `w15:done="1"`; the starter was not altered to manufacture the named section.
- Word has an independent nested Git repository beneath the actual Excel Git root. Always use the Word working directory for Git writes; the parent lists MosWord2019/ as untracked.

## Remaining verification

- Broader Windows 10/11 classroom deployment and Office bitness/build combinations. Runtime evidence currently covers Office 2019 ProPlus retail x64 16.0.14026.20302 on this machine.
- Phase 4B actual desktop UI was verified at 125%. EN/VI font-layout simulations at 100/125/150% passed; actual OS 100/150% configurations and multi-monitor transitions still require classroom acceptance. A minimum trainer height can exceed 25% on small/high-scale working areas to keep task text usable.
- Placement uses only the owned document window and retained live process handle. If placement fails, Training remains available and shows a localized manual-arrangement message. Unexpected cleanup/ownership protection still follows Phase 2 rules.
- Word unavailability was tested via the sandbox's missing COM registration, not by uninstalling Word or changing system registration.
