# Vietnamese installer translation

`Vietnamese.isl` is the unmodified, user-contributed Vietnamese translation distributed in the official Inno Setup source repository, release tag `is-6_6_1`. The upstream translation index lists Vietnamese under **Unofficial translations**. MOS Word 2019 does not claim authorship or official translation status.

- Upstream translation index: https://jrsoftware.org/files/istrans/
- Translation source: https://raw.githubusercontent.com/jrsoftware/issrc/is-6_6_1/Files/Languages/Unofficial/Vietnamese.isl
- Matching license source: https://raw.githubusercontent.com/jrsoftware/issrc/is-6_6_1/license.txt
- Retrieved: 2026-11-04.
- Translator credited by the original file: Memecoder (`memecoder17@gmail.com`). The upstream index also credits earlier translations by Vu Khac Hiep, Phung Tien Duong, and Le Duy Quang.

The original translation header, encoding and bytes are preserved. No compatibility changes were required for Inno Setup 6.6.1. The matching upstream `LICENSE-Inno-Setup.txt` is also unmodified, retaining the copyright notices and all four redistribution conditions. The translation header does not specify a separate license; the accompanying upstream license states that it covers included documentation and software except where otherwise noted.

SHA-256 of the vendored upstream files:

```text
826bc509b612d96ccfec89b81956640a3b56b547d194404e5ff95ae1a85babf0  Vietnamese.isl
92f662bffd07b3d432e1399412877da2419e4962128a0a8f4db7a0522a6c9f62  LICENSE-Inno-Setup.txt
```

Product-specific messages and the Windows requirement override remain in `Installer/MosWord2019.iss`; they do not modify the upstream translation. The file is a compiler input, not an additional runtime payload file. Validate with `server/deploy/tests/windows-installer-definition.ps1`; its optional compiler check never launches Setup or Uninstall.
