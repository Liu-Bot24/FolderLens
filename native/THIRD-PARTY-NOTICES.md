# Third-party components in the local candidate

FolderLens uses Microsoft .NET, Windows App SDK, Win2D, WebView2, SQLite, CommunityToolkit.Mvvm, NetVips/libvips, Magick.NET/ImageMagick, LibRaw, Markdig and FFmpeg. Its installer is produced with Inno Setup by Jordan Russell and Martijn Laan, https://jrsoftware.org/.

The generated `licenses/` directory contains the notices available in the exact restored NuGet packages, .NET SDK, native SDK and installer. `SBOM.json` records package IDs, versions, NuGet content hashes, native binary hashes and included static libvips dependency versions. `native/dependency-lock.json` records acquisition and build configuration evidence.

This is an unsigned local candidate, not a cleared public distribution. The owner's application license is undecided. FFmpeg is the actual Gyan 8.0.1 full static build with GPL and version 3 enabled; the upstream FFmpeg source alone does not supply the source for every statically linked codec. Its original archive hash and exact complete corresponding source have not yet been captured. NetVips native and Magick.NET bundle further codecs; their notices and version inventory are included, and remaining source/redistribution obligations are listed in the SBOM. Process separation does not remove redistribution obligations.

LibRaw 0.22.2's verified Windows SDK includes source, COPYRIGHT, LGPL 2.1 and CDDL 1.0 texts. Both upstream license alternatives are retained; this document does not choose the application license. Microsoft runtime files and the CRT are copied only from the verified SDK/runtime or official Visual Studio redistributable directory.

The bundled fixed WebView2 runtime is version 153.0.4234.32. Its security updates require a new FolderLens package. No dependency is downloaded or updated by the package script or by application startup.
