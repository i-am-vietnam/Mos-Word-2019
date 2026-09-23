# Known Issues and Verification Items

Reviewed: 2026-09-23.

No unresolved defect was observed in the final Phase 2 lifecycle matrix on the local Office 2019 x64 installation.

## Current constraints

- Calls are synchronous and must run on the same STA thread that created WordController. There is no COM-call watchdog, crash-resume, or background automation worker.
- CloseDocument and Close discard unsaved changes. Call Save explicitly first. Save failure leaves the live document available rather than closing it automatically.
- A reproduced backing-file read-only failure caused Word to retain read-only document state after the filesystem attribute was restored. Automatic save retry is not guaranteed; recover needed content through Word (for example Save As) before explicitly closing/discarding it.
- If another document is opened in the owned Word application, Close preserves that application and reports the reason. The same conservative preservation applies when remaining document ownership cannot be inspected. It relinquishes the process handle; the user must close the remaining Word documents. This intentional protection is not an orphan-cleanup success claim.
- OpenDocument supports existing writable, unencrypted .docx working copies only. TrainingWorkspaceService can derive `.docx` or `.docm` working names from approved package starters, but Phase 2 does not yet open `.docm`; enabling macro documents requires an explicit lifecycle/security decision.
- PID capture requires a visible Word OpusApp window on an interactive Windows desktop. The registered Word 16.0 library 8.7 and Office Core 2.8 PIAs are build prerequisites; no hard-coded PIA filesystem path is committed.
- Package infrastructure exists, but no production learner package or real MOS task content has been supplied. The validator checks structure and requires assertionType text; it deliberately does not claim any assertion is supported.
- Package validation checks declared files, JSON, IDs, languages and task keys without opening Word. It does not yet inspect the starter as an OOXML ZIP package; the disposable starter was independently created and opened with Word.
- ProjectLoader omits invalid packages from its returned list. Call ProjectValidator directly when the UI needs detailed package diagnostics.
- The Training workspace service has no awareness of open COM documents. Phase 4 must close the active Word document before ResetWorkingCopy.
- No grading, authentication, Training UI workflow, Testing Mode, database, or installer exists. The UI shell does not yet call the package or lifecycle services.
- Word has an independent nested Git repository beneath the actual Excel Git root. Always use the Word working directory for Git writes; the parent lists MosWord2019/ as untracked.

## Remaining verification

- Broader Windows 10/11 classroom deployment and Office bitness/build combinations. Runtime evidence currently covers Office 2019 ProPlus retail x64 16.0.14026.20302 on this machine.
- Visual/high-DPI acceptance and full EN/VI localization remain later-phase work.
- Word unavailability was tested via the sandbox's missing COM registration, not by uninstalling Word or changing system registration.
