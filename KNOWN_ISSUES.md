# Known Issues and Verification Items

Reviewed: 2026-09-23.

No unresolved defect was observed in the final Phase 2 lifecycle matrix on the local Office 2019 x64 installation.

## Current constraints

- Calls are synchronous and must run on the same STA thread that created WordController. There is no COM-call watchdog, crash-resume, or background automation worker.
- CloseDocument and Close discard unsaved changes. Call Save explicitly first. Save failure leaves the live document available rather than closing it automatically.
- A reproduced backing-file read-only failure caused Word to retain read-only document state after the filesystem attribute was restored. Automatic save retry is not guaranteed; recover needed content through Word (for example Save As) before explicitly closing/discarding it.
- If another document is opened in the owned Word application, Close preserves that application and reports the reason. The same conservative preservation applies when remaining document ownership cannot be inspected. It relinquishes the process handle; the user must close the remaining Word documents. This intentional protection is not an orphan-cleanup success claim.
- OpenDocument supports existing writable, unencrypted .docx working copies only. It rejects the literal filename starter.docx and never creates a replacement. General source-package provenance/working-copy creation belongs to Phase 3/4.
- PID capture requires a visible Word OpusApp window on an interactive Windows desktop. The registered Word 16.0 library 8.7 and Office Core 2.8 PIAs are build prerequisites; no hard-coded PIA filesystem path is committed.
- No packages, grading, authentication, Training/Testing workflows, database, or installer exist. The UI shell does not yet call the lifecycle engine.
- Word has an independent nested Git repository beneath the actual Excel Git root. Always use the Word working directory for Git writes; the parent lists MosWord2019/ as untracked.

## Remaining verification

- Broader Windows 10/11 classroom deployment and Office bitness/build combinations. Runtime evidence currently covers Office 2019 ProPlus retail x64 16.0.14026.20302 on this machine.
- Visual/high-DPI acceptance and full EN/VI localization remain later-phase work.
- Word unavailability was tested via the sandbox's missing COM registration, not by uninstalling Word or changing system registration.
