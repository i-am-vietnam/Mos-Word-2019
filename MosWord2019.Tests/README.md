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

Every fixture loads its original production task through `ProjectLoader`; it never replaces task metadata. External-output fixtures create files only under a per-test `bin/<configuration>/TestWorkspaces/<guid>` directory and use an internal, per-instance Documents-root seam. The public grader constructor and public path resolvers retain their existing defaults. These tests preserve the current existence-only semantics; session provenance, cleanup, registry resolution and crash recovery are not implemented in A1.

Package-copy verification compares every copied filename and SHA-256 with the immutable source packages. EN and VI validation covers all 28 packages / 212 tasks. Context snapshots are retained test inputs, not fresh observations of a running Word application. No manual Word/UI/lifecycle verification is implied by a headless test result.
