# Headless regression tests

This classic .NET Framework 4.7.2 / C# 7.3 project runs with Visual Studio VSTest. It references Core and Projects only; Word, Office Interop and the WinForms application are not loaded. Restore `packages.config` dependencies into the repository's ignored `packages/` directory before building. MSTest framework/adapter are pinned to 3.1.1; the production Newtonsoft.Json version remains 13.0.4.

From the Word repository root, in PowerShell:

```powershell
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$vstest = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\Extensions\TestPlatform\vstest.console.exe'
& $msbuild MosWord2019.slnx /t:Rebuild /p:Configuration=Debug '/p:Platform=Any CPU' /m
& $vstest MosWord2019.Tests\bin\Debug\MosWord2019.Tests.dll /Framework:net472 /TestAdapterPath:MosWord2019.Tests\bin\Debug '/Logger:trx;LogFileName=A1-Debug.trx' /ResultsDirectory:artifacts\a1\TestResults
```

Repeat with `Release` in the configuration, DLL/adapter paths and result filename. Each fixture row is independently discovered. Missing fixtures, incorrect expectations, oversized files, assertion coverage gaps and invalid packages fail the suite; no test is silently skipped. Fixture provenance and explicit context recipes are described in [Fixtures/README.md](Fixtures/README.md).

Answer/near-miss fixtures load their original production task through `ProjectLoader` without replacing metadata. A2 metadata-error tests explicitly clone a loaded task and alter one declared field; production package files remain unchanged. External-output fixtures create files only under a per-test `bin/<configuration>/TestWorkspaces/<guid>` directory and use an internal, per-instance Documents-root seam. The same seam isolates the existing plain-text export reader. A1/A2 left production output semantics unchanged. A3a adds required immutable ownership context: tests create a ledger before writing declared output bytes. Existing fixture recipes now supply that context without changing saved fixture bytes. Resolver tests inject registry reads; ledger/session tests use actual isolated Windows file handles, never personal Documents.

Package-copy verification compares every copied filename and SHA-256 with the immutable source packages. EN and VI validation covers all 28 packages / 212 tasks. Context snapshots are retained test inputs, not fresh observations of a running Word application. No manual Word/UI/lifecycle verification is implied by a headless test result.

A2 adds exception-injection/metadata-error cases, TXT metadata/content controls, package rejection/count/logging tests, and correct/near-miss pairs for every task using BodyTextReplaceAll, TextReplaceAll, TextRangeFormattingEquals, Footnote or SymbolInserted. Run the same commands above; all tests, including the original171, are discovered. The inventory guards remain28 packages /212 tasks /77 used types. When adding a package/task, update the named `TestInventory` constants in `TestCaseData.cs` only after adding fixtures and checking output hashes; guard failure messages identify the exact constant.

A3a adds freshness/content controls, malformed snapshot errors, redirected PersonalTemplates, restart baselines, P20/P07 overlap and exclusive cleanup protection for pre-existing/modified/replaced/locked/live/unclaimed outputs. P16 arbitrary-folder export is explicitly excluded. No Word/COM/UI or A3b crash-recovery result is implied.

A3b adds journal reload/malformed/atomic-failure/lock tests, exact reserved workspace cleanup, default/P12/P26 asset recovery, monotonic-clock and identical-overwrite controls, kernel mutex contention/abandonment and fatal cleanup sequencing. All paths are injected under TestWorkspace. These headless tests neither activate Word nor verify its native save events or actual fatal-process UI; retained native/API results must be labeled separately.

A4 adds a separate `MosWord2019.Desktop.Tests` classic net472 MSTest project. The original assembly still has no Office/UI references and its dependency guard is unchanged. Desktop tests reference the existing application and use fake controllers/registrars on isolated STAs; they never activate Word. One test registers/restores the actual native OLE filter without Office. Both test assemblies use the existing pinned packages.config versions. Run all checks together:

```powershell
& $vstest MosWord2019.Tests\bin\Debug\MosWord2019.Tests.dll MosWord2019.Desktop.Tests\bin\Debug\MosWord2019.Desktop.Tests.dll /Framework:net472 /TestAdapterPath:MosWord2019.Tests\bin\Debug '/Logger:trx;LogFileName=A4-Debug.trx' /ResultsDirectory:artifacts\a4\TestResults
```

Repeat for Release. The Vietnamese real-Word checklist is `artifacts/a4/MANUAL_TEST.md`; it is deliberately outside tracked fixtures and is not a completed native test report.
